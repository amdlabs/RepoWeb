using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Controllers;

public sealed record SuscripcionRequest(string Endpoint, string P256dh, string Auth);

/// <summary>
/// Avisos push: suscripción de dispositivos y foto de cada aviso.
/// La foto se sirve con una ficha firmada porque la notificación la carga el
/// sistema del teléfono, fuera de la sesión de la aplicación.
/// </summary>
[ApiController]
[Route("api/push")]
public sealed class PushApiController : ControllerBase
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly PushService _push;
    private readonly SnapshotPathResolver _paths;

    public PushApiController(IDbContextFactory<VisionDbContext> dbFactory, PushService push,
                             SnapshotPathResolver paths)
    {
        _dbFactory = dbFactory;
        _push = push;
        _paths = paths;
    }

    /// <summary>Clave pública con la que el navegador se suscribe.</summary>
    [HttpGet("clave")]
    [Authorize]
    public async Task<IActionResult> Clave(CancellationToken ct)
        => Ok(new { clave = await _push.GetPublicKeyAsync(ct) });

    /// <summary>Alta (o refresco) de la suscripción de este dispositivo.</summary>
    [HttpPost("suscribir")]
    [Authorize]
    public async Task<IActionResult> Suscribir([FromBody] SuscripcionRequest datos, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(datos.Endpoint) || string.IsNullOrWhiteSpace(datos.P256dh)
            || string.IsNullOrWhiteSpace(datos.Auth))
            return BadRequest(new { error = "Suscripción incompleta." });

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existente = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == datos.Endpoint, ct);
        if (existente is null)
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                Endpoint = datos.Endpoint,
                P256dh = datos.P256dh,
                Auth = datos.Auth,
                Username = User.Identity?.Name,
            });
        }
        else
        {
            existente.P256dh = datos.P256dh;
            existente.Auth = datos.Auth;
            existente.Username = User.Identity?.Name;
            existente.FailCount = 0;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    /// <summary>Baja de la suscripción de este dispositivo.</summary>
    [HttpPost("baja")]
    [Authorize]
    public async Task<IActionResult> Baja([FromBody] SuscripcionRequest datos, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var borradas = await db.PushSubscriptions.Where(s => s.Endpoint == datos.Endpoint).ExecuteDeleteAsync(ct);
        return Ok(new { ok = true, borradas });
    }

    /// <summary>
    /// Foto del evento para la notificación. Sin sesión, pero con ficha firmada:
    /// sólo quien recibió el aviso conoce la dirección exacta.
    /// </summary>
    [HttpGet("foto/{id:long}")]
    [AllowAnonymous]
    public async Task<IActionResult> Foto(long id, [FromQuery] string? k, [FromQuery] bool escena,
                                          CancellationToken ct)
    {
        if (!_push.ValidatePhotoToken(id, k)) return NotFound();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var evento = await db.RecognitionEvents.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.CropBase64, e.CropPath, e.FullFramePath })
            .FirstOrDefaultAsync(ct);

        if (evento is null) return NotFound();

        if (escena && evento.FullFramePath is not null)
        {
            var completa = _paths.Resolve(evento.FullFramePath);
            if (completa is not null && System.IO.File.Exists(completa))
                return PhysicalFile(completa, "image/jpeg");
        }

        if (evento.CropBase64 is not null)
            return File(Convert.FromBase64String(evento.CropBase64), "image/jpeg");

        if (evento.CropPath is not null)
        {
            var ruta = _paths.Resolve(evento.CropPath);
            if (ruta is not null && System.IO.File.Exists(ruta))
                return PhysicalFile(ruta, "image/jpeg");
        }

        return NotFound();
    }
}
