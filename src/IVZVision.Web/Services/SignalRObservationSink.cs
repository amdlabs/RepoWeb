using IVZVision.Core.Detection;
using IVZVision.Vision.Pipeline;
using IVZVision.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace IVZVision.Web.Services;

/// <summary>Payload que recibe el navegador por cada reconocimiento.</summary>
public sealed record ObservationDto(
    string Id,
    string Tipo,
    string CamaraId,
    string Camara,
    string Hora,
    string Etiqueta,
    string? Matricula,
    bool Conocido,
    bool Autorizado,
    int Confianza,
    int Similitud,
    string? Miniatura,
    long? EventoId,
    string? Detalle,
    string? Escena = null,
    // Recuadro del sujeto en porcentaje del fotograma: así el navegador lo sitúa
    // sobre la imagen en vivo sin saber a qué resolución se está emitiendo.
    double? CajaX = null,
    double? CajaY = null,
    double? CajaAncho = null,
    double? CajaAlto = null,
    /// <summary>Grupo de rostros al que pertenece, para poder agruparlo o etiquetarlo desde el muro.</summary>
    int? GrupoId = null);

public sealed record CameraStatusDto(
    string CamaraId,
    string Nombre,
    bool Conectada,
    string Estado,
    string? Error,
    double Fps,
    string Resolucion,
    long Fotogramas);

/// <summary>Reenvía a SignalR lo que produce el pipeline de visión.</summary>
public sealed class SignalRObservationSink : IObservationSink
{
    private readonly IHubContext<DetectionHub> _hub;

    public SignalRObservationSink(IHubContext<DetectionHub> hub) => _hub = hub;

    public async Task OnObservationAsync(Observation observation, CancellationToken ct = default)
    {
        var dto = ToDto(observation);

        await _hub.Clients.Group(DetectionHub.AllCamerasGroup)
                  .SendAsync("deteccion", dto, ct).ConfigureAwait(false);
    }

    public async Task OnCameraStatusAsync(CameraStatus status, CancellationToken ct = default)
    {
        var dto = new CameraStatusDto(
            status.CameraId.ToString(),
            status.Name,
            status.Connected,
            status.State,
            status.LastError,
            status.MeasuredFps,
            status.FrameWidth > 0 ? $"{status.FrameWidth}×{status.FrameHeight}" : "—",
            status.FramesProcessed);

        await _hub.Clients.Group(DetectionHub.AllCamerasGroup)
                  .SendAsync("estadoCamara", dto, ct).ConfigureAwait(false);
    }

    public static ObservationDto ToDto(Observation o) => new(
        o.Id,
        o.Kind switch
        {
            ObservationKind.Plate => "matricula",
            ObservationKind.Object => "objeto",
            ObservationKind.Text => "texto",
            _ => "rostro",
        },
        o.CameraId.ToString(),
        o.CameraName,
        o.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
        o.Kind switch
        {
            ObservationKind.Plate => o.PlateText ?? "?",
            ObservationKind.Object => o.DisplayLabel,
            _ => o.Match.Label,
        },
        o.PlateText,
        o.Match.IsKnown,
        o.Match.IsKnown && o.Match.IsAuthorized,
        (int)Math.Round(o.DetectionScore * 100),
        (int)Math.Round((o.Kind == ObservationKind.Plate ? (o.OcrConfidence ?? 0) : o.Match.Score) * 100),
        o.CropJpegBase64 is null ? null : $"data:image/jpeg;base64,{o.CropJpegBase64}",
        o.EventId,
        o.Kind switch
        {
            ObservationKind.Plate when o.Match.IsKnown => o.Match.Label,
            ObservationKind.Object => o.ObjectClass,
            _ => o.Match.Notes,
        },
        // Escena completa: da contexto al abrir la detección con doble clic.
        o.FullFramePath is null ? null : $"/media/recorte?path={Uri.EscapeDataString(o.FullFramePath)}",
        o.FrameWidth > 0 ? o.Box.X * 100.0 / o.FrameWidth : null,
        o.FrameHeight > 0 ? o.Box.Y * 100.0 / o.FrameHeight : null,
        o.FrameWidth > 0 ? o.Box.Width * 100.0 / o.FrameWidth : null,
        o.FrameHeight > 0 ? o.Box.Height * 100.0 / o.FrameHeight : null,
        o.FaceClusterId);
}
