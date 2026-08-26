using IVZVision.Vision.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IVZVision.Web.Controllers;

/// <summary>
/// Encendido y apagado del motor de reconocimiento desde la web. La aplicación
/// (y por tanto la web) sigue siempre accesible: lo que arranca o se detiene es
/// la captura y el análisis de las cámaras. La decisión queda guardada en la
/// configuración, así que se respeta también tras reiniciar el equipo.
/// </summary>
[ApiController]
[Route("api/motor")]
public class MotorApiController : ControllerBase
{
    private readonly CameraPipelineManager _pipeline;

    public MotorApiController(CameraPipelineManager pipeline) => _pipeline = pipeline;

    [HttpGet("estado")]
    [Authorize]
    public IActionResult Estado()
    {
        var estados = _pipeline.Statuses;

        return Ok(new
        {
            encendido = _pipeline.EngineEnabled,
            enMarcha = _pipeline.EngineRunning,
            camaras = estados.Count,
            conectadas = estados.Count(s => s.Connected),
            fps = estados.Count == 0 ? 0 : Math.Round(estados.Average(s => s.MeasuredFps), 1),
        });
    }

    public sealed class MotorRequest
    {
        public bool Encendido { get; set; }
    }

    /// <summary>Cambia el estado del motor (sólo administradores).</summary>
    [HttpPost]
    [Authorize(Policy = "Administrador")]
    public async Task<IActionResult> Cambiar([FromBody] MotorRequest request, CancellationToken ct)
    {
        await _pipeline.SetEngineEnabledAsync(request.Encendido, ct);

        return Ok(new
        {
            ok = true,
            encendido = request.Encendido,
            mensaje = request.Encendido
                ? "Motor encendido. Las cámaras están arrancando; seguirá encendido tras reiniciar el equipo."
                : "Motor apagado. La web sigue accesible y permanecerá apagado tras reiniciar el equipo.",
        });
    }
}
