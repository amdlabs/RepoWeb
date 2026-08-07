using IVZVision.Core.Configuration;
using IVZVision.Vision.Pipeline;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IVZVision.Web.Controllers;

/// <summary>
/// Datos de arranque de la vista en directo (cámaras, estados y detecciones
/// recientes). Requiere sesión: lo consume live.js, no las integraciones externas.
/// </summary>
[ApiController]
[Authorize]
[Route("api/directo")]
public class DirectoApiController : ControllerBase
{
    private readonly IConfigStore _config;
    private readonly CameraPipelineManager _pipeline;

    public DirectoApiController(IConfigStore config, CameraPipelineManager pipeline)
    {
        _config = config;
        _pipeline = pipeline;
    }

    [HttpGet("estado")]
    public IActionResult Estado()
    {
        var camaras = _config.Current.Cameras
            .Where(c => c.Enabled)
            .Select(c => new { id = c.Id.ToString(), nombre = c.Name })
            .ToList();

        var estados = _pipeline.Statuses.Select(s => new CameraStatusDto(
            s.CameraId.ToString(), s.Name, s.Connected, s.State, s.LastError,
            s.MeasuredFps,
            s.FrameWidth > 0 ? $"{s.FrameWidth}×{s.FrameHeight}" : "—",
            s.FramesProcessed)).ToList();

        var recientes = _pipeline.GetRecentObservations(take: 30)
            .Select(SignalRObservationSink.ToDto)
            .ToList();

        return Ok(new { camaras, estados, recientes });
    }
}
