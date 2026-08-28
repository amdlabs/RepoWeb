using IVZVision.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IVZVision.Web.Controllers;

public sealed record UnificarRostrosRequest(int[] Grupos);
public sealed record EtiquetarRostroRequest(int GrupoId, string Nombre);

/// <summary>
/// Acciones sobre los grupos de rostros desde el muro de monitoreo: unir los que
/// son la misma persona y ponerles nombre sin salir de la pantalla de vigilancia.
/// </summary>
[ApiController]
[Route("api/rostros")]
[Authorize(Policy = "Operador")]
public sealed class RostrosApiController : ControllerBase
{
    private readonly FaceGroupService _grupos;

    public RostrosApiController(FaceGroupService grupos) => _grupos = grupos;

    /// <summary>Une varios grupos en uno y recalcula su cara promedio.</summary>
    [HttpPost("unificar")]
    public async Task<IActionResult> Unificar([FromBody] UnificarRostrosRequest peticion, CancellationToken ct)
    {
        var resultado = await _grupos.UnificarAsync(peticion.Grupos ?? Array.Empty<int>(), ct);
        return resultado.Ok
            ? Ok(new { ok = true, mensaje = resultado.Mensaje, grupoId = resultado.GrupoId, nombre = resultado.Nombre })
            : BadRequest(new { ok = false, mensaje = resultado.Mensaje });
    }

    /// <summary>Pone nombre a un grupo y aprende de sus fotos.</summary>
    [HttpPost("etiquetar")]
    public async Task<IActionResult> Etiquetar([FromBody] EtiquetarRostroRequest peticion, CancellationToken ct)
    {
        var resultado = await _grupos.NombrarAsync(peticion.GrupoId, peticion.Nombre ?? "", ct);
        return resultado.Ok
            ? Ok(new { ok = true, mensaje = resultado.Mensaje, nombre = resultado.Nombre })
            : BadRequest(new { ok = false, mensaje = resultado.Mensaje });
    }
}
