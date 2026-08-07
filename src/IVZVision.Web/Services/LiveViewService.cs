using IVZVision.Core.Detection;
using IVZVision.Vision.Engine;
using IVZVision.Vision.Pipeline;

namespace IVZVision.Web.Services;

/// <summary>Un objeto que la cámara está viendo en este instante.</summary>
public sealed record VistaObjeto(
    string tipo,
    string etiqueta,
    bool conocido,
    bool autorizado,
    double confianza,
    double similitud,
    Cuadro cuadro,
    string? matricula = null,
    string? clase = null,
    string? codigo = null,
    string? formatoCodigo = null,
    string? texto = null,
    string? alerta = null,
    string? gravedad = null,
    string? motivo = null,
    int? seguimiento = null,
    long? personaId = null,
    long? vehiculoId = null,
    long? objetoId = null);

public sealed record Cuadro(int x, int y, int ancho, int alto);

public sealed record VistaCamara(
    string id,
    string nombre,
    string origen,
    bool conectada,
    string estado,
    string? error,
    double fps,
    int ancho,
    int alto,
    DateTimeOffset? ultimoFotograma,
    IReadOnlyList<VistaObjeto> viendo,
    IReadOnlyList<VistaObjeto> alertas,
    string? imagen = null);

public sealed record VistaRespuesta(
    DateTimeOffset instante,
    int camaras,
    IReadOnlyList<VistaCamara> detalle,
    EstadoMotor motor);

public sealed record EstadoMotor(
    bool rostros,
    bool matriculas,
    bool objetos,
    bool codigos,
    bool texto);

/// <summary>
/// Construye la foto instantánea de lo que ven las cámaras. La comparten la API
/// REST (<c>/api/ver</c>) y las herramientas MCP, para que ambas devuelvan
/// exactamente lo mismo.
/// </summary>
public sealed class LiveViewService
{
    private readonly CameraPipelineManager _pipeline;
    private readonly RecognitionEngine _engine;
    private readonly FrameBroadcaster _broadcaster;

    public LiveViewService(CameraPipelineManager pipeline, RecognitionEngine engine, FrameBroadcaster broadcaster)
    {
        _pipeline = pipeline;
        _engine = engine;
        _broadcaster = broadcaster;
    }

    public VistaRespuesta Build(Guid? cameraId = null, bool includeImage = false, string? kindFilter = null)
    {
        var snapshot = _pipeline.Snapshot(cameraId);
        var cameras = new List<VistaCamara>(snapshot.Count);

        foreach (var (status, camera, seeing) in snapshot)
        {
            var visible = seeing
                .Where(o => o.Kind != ObservationKind.Activity)
                .Where(o => MatchesFilter(o, kindFilter))
                .Select(ToDto)
                .ToList();

            var alerts = seeing
                .Where(o => o.Kind == ObservationKind.Activity)
                .Select(ToDto)
                .ToList();

            cameras.Add(new VistaCamara(
                camera.Id.ToString(),
                camera.Name,
                camera.DescribeSource(),
                status.Connected,
                status.State,
                status.LastError,
                status.MeasuredFps,
                status.FrameWidth,
                status.FrameHeight,
                status.LastFrameAt,
                visible,
                alerts,
                includeImage ? EncodeLatest(camera.Id) : null));
        }

        var engine = _engine.Status;

        return new VistaRespuesta(
            DateTimeOffset.Now,
            cameras.Count,
            cameras,
            new EstadoMotor(engine.FacesAvailable, engine.PlatesAvailable, engine.ObjectsAvailable,
                            engine.CodesAvailable, engine.TextAvailable));
    }

    private string? EncodeLatest(Guid cameraId)
    {
        var jpeg = _broadcaster.GetLatest(cameraId);
        return jpeg is null ? null : $"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}";
    }

    private static bool MatchesFilter(Observation o, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        return filter.Trim().ToLowerInvariant() switch
        {
            "rostro" or "rostros" or "cara" or "caras" => o.Kind == ObservationKind.Face,
            "matricula" or "matriculas" => o.Kind == ObservationKind.Plate,
            "objeto" or "objetos" => o.Kind == ObservationKind.Object,
            "codigo" or "codigos" or "qr" => o.Kind == ObservationKind.Code,
            "texto" => o.Kind == ObservationKind.Text,
            _ => true,
        };
    }

    public static VistaObjeto ToDto(Observation o) => new(
        tipo: DescribeKind(o.Kind),
        etiqueta: o.DisplayLabel,
        conocido: o.Match.IsKnown,
        autorizado: o.Match.IsKnown && o.Match.IsAuthorized,
        confianza: Math.Round(o.DetectionScore, 3),
        similitud: Math.Round(o.Kind == ObservationKind.Plate ? (o.OcrConfidence ?? 0) : o.Match.Score, 3),
        cuadro: new Cuadro((int)o.Box.X, (int)o.Box.Y, (int)o.Box.Width, (int)o.Box.Height),
        matricula: o.PlateText,
        clase: o.ObjectClass,
        codigo: o.CodeValue,
        formatoCodigo: o.CodeFormat,
        texto: o.TextValue,
        alerta: o.Kind == ObservationKind.Activity ? o.Activity.ToString() : null,
        gravedad: o.Kind == ObservationKind.Activity ? o.Severity.ToString() : null,
        motivo: o.Explanation,
        seguimiento: o.TrackId,
        personaId: o.Match.PersonId,
        vehiculoId: o.Match.VehicleId,
        objetoId: o.Match.ObjectId);

    public static string DescribeKind(ObservationKind kind) => kind switch
    {
        ObservationKind.Face => "rostro",
        ObservationKind.Plate => "matricula",
        ObservationKind.Object => "objeto",
        ObservationKind.Code => "codigo",
        ObservationKind.Text => "texto",
        ObservationKind.Activity => "alerta",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
