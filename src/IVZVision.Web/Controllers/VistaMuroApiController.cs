using IVZVision.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IVZVision.Web.Controllers;

public sealed record VistaMuroRequest(int Layout, string[] Camaras);

/// <summary>
/// Vista del muro (qué cámara va en cada recuadro y con qué distribución), guardada
/// en el servidor por usuario para que sobreviva al navegador y al dispositivo: si
/// el usuario coloca sus cámaras, las encuentra igual entrando desde otro equipo.
/// </summary>
[ApiController]
[Route("api/vista-muro")]
[Authorize]
public sealed class VistaMuroApiController : ControllerBase
{
    private readonly IConfigStore _config;

    public VistaMuroApiController(IConfigStore config) => _config = config;

    private string Usuario => User.Identity?.Name ?? "anon";

    [HttpGet]
    public IActionResult Obtener()
    {
        var vista = _config.Current.WallViews.FirstOrDefault(v => v.Username == Usuario);
        if (vista is null) return Ok(new { layout = 0, camaras = Array.Empty<string>() });
        return Ok(new { layout = vista.Layout, camaras = vista.CameraOrder });
    }

    [HttpPost]
    public async Task<IActionResult> Guardar([FromBody] VistaMuroRequest datos, CancellationToken ct)
    {
        await _config.UpdateAsync(cfg =>
        {
            var vista = cfg.WallViews.FirstOrDefault(v => v.Username == Usuario);
            if (vista is null)
            {
                vista = new WallView { Username = Usuario };
                cfg.WallViews.Add(vista);
            }
            vista.Layout = datos.Layout;
            vista.CameraOrder = (datos.Camaras ?? Array.Empty<string>()).ToList();
        }, ct);

        return Ok(new { ok = true });
    }
}
