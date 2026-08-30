using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Pipeline;
using Microsoft.AspNetCore.Mvc;

namespace IVZVision.Web.Controllers;

/// <summary>
/// API JSON para integraciones externas:
///  - GET /api/camaras                      → lista de cámaras configuradas y su estado.
///  - GET /api/camaras/{id}/detecciones     → últimos objetos/rostros/matrículas de esa cámara.
/// Autenticación: sesión iniciada (navegador) o cabecera <c>X-Api-Key</c> con la clave
/// definida en Configuración → Seguridad. Sin clave configurada, sólo sesiones.
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

    /// <summary>Sesión iniciada, o clave de API válida en la cabecera X-Api-Key.</summary>
    private bool IsAllowed()
    {
        if (User.Identity?.IsAuthenticated == true) return true;

        var configured = _config.Current.Security.ApiKey;
        if (string.IsNullOrEmpty(configured)) return false;

        return Request.Headers.TryGetValue("X-Api-Key", out var provided)
               && string.Equals(provided.ToString(), configured, StringComparison.Ordinal);
    }

    private IActionResult Denied() =>
        Unauthorized(new { error = "Falta la sesión o la cabecera X-Api-Key con una clave válida." });

    [HttpGet]
    public IActionResult List()
    {
        if (!IsAllowed()) return Denied();

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
                leeTextos = c.EnableTextReading,
                zonas = c.Zones.Select(z => new
                {
                    x = z.XPercent, y = z.YPercent, ancho = z.WidthPercent, alto = z.HeightPercent,
                }),
                conectada = status?.Connected ?? false,
                estado = status?.State ?? "Parada",
                fps = status?.MeasuredFps ?? 0,
                fpsAnalisis = status?.MeasuredAnalysisFps ?? 0,
                resolucion = status is { FrameWidth: > 0 } ? $"{status.FrameWidth}x{status.FrameHeight}" : null,
                ultimoFotograma = status?.LastFrameAt,
                error = status?.LastError,
            };
        });

        return Ok(cameras);
    }

    public sealed record ReconocimientosRequest(bool? Rostros, bool? Matriculas, bool? Objetos, bool? Textos);

    /// <summary>
    /// Enciende o apaga los reconocimientos de una cámara desde el muro, sin pasar por
    /// la pantalla de configuración. Sólo cambia lo que venga informado en el cuerpo.
    /// </summary>
    [HttpPost("{id:guid}/reconocimientos")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "Administrador")]
    public async Task<IActionResult> Reconocimientos(Guid id, [FromBody] ReconocimientosRequest cambios,
                                                     CancellationToken ct)
    {
        if (_config.Current.FindCamera(id) is null)
            return NotFound(new { error = "La cámara indicada no existe." });

        var actualizado = await _config.UpdateAsync(cfg =>
        {
            var cam = cfg.FindCamera(id);
            if (cam is null) return;

            if (cambios.Rostros is bool r) cam.EnableFaceRecognition = r;
            if (cambios.Matriculas is bool m) cam.EnablePlateRecognition = m;
            if (cambios.Objetos is bool o) cam.EnableObjectDetection = o;
            if (cambios.Textos is bool t) cam.EnableTextReading = t;
        }, ct);

        var cam2 = actualizado.FindCamera(id)!;
        return Ok(new
        {
            ok = true,
            rostros = cam2.EnableFaceRecognition,
            matriculas = cam2.EnablePlateRecognition,
            objetos = cam2.EnableObjectDetection,
            textos = cam2.EnableTextReading,
        });
    }

    /// <summary>Devuelve las últimas detecciones de la cámara indicada.</summary>
    /// <param name="id">Id de la cámara (véase GET /api/camaras).</param>
    /// <param name="take">Número máximo de detecciones (1-200, por defecto 50).</param>
    /// <param name="incluirImagen">Incluye el recorte JPEG en base64 de cada detección.</param>
    [HttpGet("{id:guid}/detecciones")]
    public IActionResult Detections(Guid id, [FromQuery] int take = 50, [FromQuery] bool incluirImagen = false)
    {
        if (!IsAllowed()) return Denied();

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
