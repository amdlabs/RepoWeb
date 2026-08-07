using IVZVision.Core.Util;
using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Controllers;

/// <summary>
/// Completar los datos de una detección desde el diálogo del Monitoreo:
/// dar nombre a una persona, registrar un vehículo o categorizar un objeto.
/// Requiere sesión con rol operador o administrador.
/// </summary>
[ApiController]
[Authorize(Policy = "Operador")]
[Route("api/detecciones")]
public class DeteccionesApiController : ControllerBase
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly KnownSubjectsIndex _index;
    private readonly EnrollmentService _enrollment;
    private readonly SnapshotPathResolver _snapshots;

    public DeteccionesApiController(IDbContextFactory<VisionDbContext> dbFactory, KnownSubjectsIndex index,
                                    EnrollmentService enrollment, SnapshotPathResolver snapshots)
    {
        _dbFactory = dbFactory;
        _index = index;
        _enrollment = enrollment;
        _snapshots = snapshots;
    }

    public sealed class PersonaRequest
    {
        public long EventoId { get; set; }
        public string Nombre { get; set; } = "";
    }

    /// <summary>Crea una persona a partir del recorte del evento y registra su rostro.</summary>
    [HttpPost("persona")]
    public async Task<IActionResult> Persona([FromBody] PersonaRequest request, CancellationToken ct)
    {
        var nombre = (request.Nombre ?? "").Trim();
        if (nombre.Length == 0)
            return Ok(new { ok = false, mensaje = "Escriba el nombre de la persona." });

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var evento = await db.RecognitionEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == request.EventoId, ct);

        byte[]? imagen = null;
        if (evento?.CropBase64 is not null)
            imagen = Convert.FromBase64String(evento.CropBase64);
        else if (evento?.CropPath is not null)
        {
            var resolved = _snapshots.Resolve(evento.CropPath);
            if (resolved is not null && System.IO.File.Exists(resolved))
                imagen = await System.IO.File.ReadAllBytesAsync(resolved, ct);
        }

        if (imagen is null)
            return Ok(new { ok = false, mensaje = "La detección no conserva su imagen; use otra más reciente." });

        var person = new Person { FullName = nombre, IsAuthorized = false };
        db.Persons.Add(person);
        await db.SaveChangesAsync(ct);

        var result = await _enrollment.EnrollFromBytesAsync(person.Id, imagen, ct);
        if (!result.Success)
        {
            db.Persons.Remove(person);
            await db.SaveChangesAsync(ct);
            return Ok(new { ok = false, mensaje = $"No se pudo registrar el rostro: {result.Message}" });
        }

        _index.MarkDirty();
        return Ok(new { ok = true, mensaje = $"Persona «{nombre}» creada con ese rostro (revísela para autorizarla)." });
    }

    /// <summary>Imagen SVG de una matrícula uruguaya con el texto leído.</summary>
    [HttpGet("/matricula/{texto}.svg")]
    [AllowAnonymous]
    public IActionResult MatriculaSvg(string texto)
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        return Content(PlateImageBuilder.BuildSvg(texto), "image/svg+xml");
    }

    public sealed class VehiculoRequest
    {
        public string Matricula { get; set; } = "";
        public string? Marca { get; set; }
        public string? Modelo { get; set; }
        public string? Notas { get; set; }
        public bool Autorizado { get; set; }
    }

    /// <summary>Registra el vehículo de una matrícula leída.</summary>
    [HttpPost("vehiculo")]
    public async Task<IActionResult> Vehiculo([FromBody] VehiculoRequest request, CancellationToken ct)
    {
        var normalized = PlateText.Normalize(request.Matricula ?? "");
        if (normalized.Length < 2)
            return Ok(new { ok = false, mensaje = "La matrícula no es válida." });

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (await db.Vehicles.AnyAsync(v => v.Plate == normalized, ct))
            return Ok(new { ok = false, mensaje = $"La matrícula {normalized} ya está registrada." });

        db.Vehicles.Add(new Vehicle
        {
            Plate = normalized,
            PlateRaw = request.Matricula?.Trim(),
            Make = string.IsNullOrWhiteSpace(request.Marca) ? null : request.Marca.Trim(),
            Model = string.IsNullOrWhiteSpace(request.Modelo) ? null : request.Modelo.Trim(),
            Notes = string.IsNullOrWhiteSpace(request.Notas) ? null : request.Notas.Trim(),
            IsAuthorized = request.Autorizado,
        });
        await db.SaveChangesAsync(ct);

        _index.MarkDirty();
        return Ok(new { ok = true, mensaje = $"Vehículo {normalized} registrado." });
    }

    public sealed class ObjetoRequest
    {
        public string Clase { get; set; } = "";
        public string Nombre { get; set; } = "";
        public bool Autorizado { get; set; } = true;
    }

    /// <summary>Asigna la categoría real a una clase de objeto detectada.</summary>
    [HttpPost("objeto")]
    public async Task<IActionResult> Objeto([FromBody] ObjetoRequest request, CancellationToken ct)
    {
        var clase = (request.Clase ?? "").Trim();
        var nombre = (request.Nombre ?? "").Trim();
        if (clase.Length == 0 || nombre.Length == 0)
            return Ok(new { ok = false, mensaje = "Faltan la clase o la categoría." });

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.ObjectLabels.FirstOrDefaultAsync(o => o.ClassName == clase, ct);
        if (existing is null)
        {
            db.ObjectLabels.Add(new ObjectLabel { ClassName = clase, DisplayName = nombre, IsAuthorized = request.Autorizado });
        }
        else
        {
            existing.DisplayName = nombre;
            existing.IsAuthorized = request.Autorizado;
            existing.IsActive = true;
        }
        await db.SaveChangesAsync(ct);

        _index.MarkDirty();
        return Ok(new { ok = true, mensaje = $"Las detecciones de «{clase}» saldrán ahora como «{nombre}»." });
    }
}
