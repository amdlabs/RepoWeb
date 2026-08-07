using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Pipeline;
using Microsoft.AspNetCore.Mvc;

namespace IVZVision.Web.Controllers;

/// <summary>
/// API JSON para integraciones externas:
///  - GET /api/camaras                      → lista de cámaras configuradas y su estado.
///  - GET /api/camaras/{id}/detecciones     → últimos objetos/rostros/matrículas de esa cámara.
/// </summary>
[ApiController]
[Route("api/camaras")]
public class CamerasApiController : ControllerBase
{
    private readonly IConfigStore _config;
    private readonly CameraPipelineManager _pipeline;

    public CamerasApiController(IConfigStore config, CameraPipelineManager pipeline)
    {
        _config = config;
        _pipeline = pipeline;
    }

    [HttpGet]
    public IActionResult List()
    {
        var cameras = _config.Current.Cameras.Select(c =>
        {
            var status = _pipeline.GetStatus(c.Id);
            return new
            {
                id = c.Id,
                nombre = c.Name,
                habilitada = c.Enabled,
                tipo = c.Vendor.ToString(),
                fuente = c.BuildRtspUrl(maskCredentials: true),
                reconoceRostros = c.EnableFaceRecognition,
                leeMatriculas = c.EnablePlateRecognition,
                detectaObjetos = c.EnableObjectDetection,
                conectada = status?.Connected ?? false,
                estado = status?.State ?? "Parada",
                fps = status?.MeasuredFps ?? 0,
                resolucion = status is { FrameWidth: > 0 } ? $"{status.FrameWidth}x{status.FrameHeight}" : null,
                ultimoFotograma = status?.LastFrameAt,
                error = status?.LastError,
            };
        });

        return Ok(cameras);
    }

    /// <summary>Devuelve las últimas detecciones de la cámara indicada.</summary>
    /// <param name="id">Id de la cámara (véase GET /api/camaras).</param>
    /// <param name="take">Número máximo de detecciones (1-200, por defecto 50).</param>
    /// <param name="incluirImagen">Incluye el recorte JPEG en base64 de cada detección.</param>
    [HttpGet("{id:guid}/detecciones")]
    public IActionResult Detections(Guid id, [FromQuery] int take = 50, [FromQuery] bool incluirImagen = false)
    {
        var camera = _config.Current.FindCamera(id);
        if (camera is null)
            return NotFound(new { error = "La cámara indicada no existe." });

        var observations = _pipeline.GetRecentObservations(id, Math.Clamp(take, 1, 200));

        return Ok(new
        {
            camaraId = id,
            camara = camera.Name,
            total = observations.Count,
            detecciones = observations.Select(o => new
            {
                id = o.Id,
                tipo = o.Kind switch
                {
                    ObservationKind.Plate => "matricula",
                    ObservationKind.Object => "objeto",
                    _ => "rostro",
                },
                fecha = o.Timestamp,
                etiqueta = o.DisplayLabel,
                clase = o.ObjectClass,
                matricula = o.PlateText,
                conocido = o.Match.IsKnown,
                autorizado = o.Match.IsKnown && o.Match.IsAuthorized,
                confianzaDeteccion = Math.Round(o.DetectionScore, 3),
                similitud = Math.Round(o.Match.Score, 3),
                confianzaOcr = o.OcrConfidence,
                cuadro = new { x = o.Box.X, y = o.Box.Y, ancho = o.Box.Width, alto = o.Box.Height },
                imagenJpegBase64 = incluirImagen ? o.CropJpegBase64 : null,
                eventoId = o.EventId,
            }),
        });
    }
}
