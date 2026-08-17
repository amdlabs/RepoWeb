using IVZVision.Core.Util;
using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Controllers;

/// <summary>
/// Corrección de lecturas de matrícula con aprendizaje: además de rectificar el
/// evento, la corrección se guarda para que el OCR la aplique en adelante.
/// </summary>
[ApiController]
[Authorize(Policy = "Operador")]
[Route("api/correcciones")]
public class CorreccionesApiController : ControllerBase
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly KnownSubjectsIndex _index;
    private readonly ILogger<CorreccionesApiController> _logger;

    public CorreccionesApiController(IDbContextFactory<VisionDbContext> dbFactory, KnownSubjectsIndex index,
                                     ILogger<CorreccionesApiController> logger)
    {
        _dbFactory = dbFactory;
        _index = index;
        _logger = logger;
    }

    public sealed class MatriculaRequest
    {
        public long EventoId { get; set; }
        public string Correcta { get; set; } = "";
        /// <summary>Corrige también el resto de eventos con la misma lectura errónea.</summary>
        public bool AplicarAlHistorico { get; set; } = true;
    }

    [HttpPost("matricula")]
    public async Task<IActionResult> Matricula([FromBody] MatriculaRequest request, CancellationToken ct)
    {
        var correcta = PlateText.Normalize(request.Correcta ?? "");
        if (correcta.Length < 2)
            return Ok(new { ok = false, mensaje = "Escriba la matrícula correcta." });

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var evento = await db.RecognitionEvents.FirstOrDefaultAsync(e => e.Id == request.EventoId, ct);
        if (evento is null)
            return Ok(new { ok = false, mensaje = "El evento ya no existe." });

        var errónea = evento.PlateText ?? "";
        if (errónea == correcta)
            return Ok(new { ok = false, mensaje = "La matrícula indicada es la que ya estaba registrada." });

        // 1) Se rectifica el evento (y su etiqueta visible).
        evento.PlateText = correcta;
        evento.Label = evento.Label.Replace(errónea, correcta, StringComparison.OrdinalIgnoreCase);
        if (!evento.Label.Contains(correcta, StringComparison.OrdinalIgnoreCase)) evento.Label = correcta;

        var rectificados = 1;

        // 2) El resto del histórico con la misma lectura errónea.
        if (request.AplicarAlHistorico && errónea.Length >= 2)
        {
            var otros = await db.RecognitionEvents
                .Where(e => e.PlateText == errónea && e.Id != evento.Id)
                .ToListAsync(ct);

            foreach (var otro in otros)
            {
                otro.PlateText = correcta;
                otro.Label = otro.Label.Replace(errónea, correcta, StringComparison.OrdinalIgnoreCase);
                if (!otro.Label.Contains(correcta, StringComparison.OrdinalIgnoreCase)) otro.Label = correcta;
            }

            rectificados += otros.Count;
        }

        // 3) Se aprende la corrección para las lecturas futuras.
        if (errónea.Length >= 2)
        {
            var learned = await db.PlateCorrections.FirstOrDefaultAsync(c => c.WrongText == errónea, ct);
            if (learned is null)
            {
                db.PlateCorrections.Add(new PlateCorrection
                {
                    WrongText = errónea,
                    CorrectText = correcta,
                    CorrectedBy = User.Identity?.Name,
                    TimesApplied = 1,
                });
            }
            else
            {
                learned.CorrectText = correcta;
                learned.TimesApplied++;
                learned.CorrectedBy = User.Identity?.Name;
            }
        }

        await db.SaveChangesAsync(ct);

        // El motor recoge la corrección en el siguiente refresco del índice.
        _index.MarkDirty();
        await _index.RefreshAsync(ct);

        _logger.LogInformation("Corrección de matrícula: «{Wrong}» → «{Right}» ({Count} evento(s))",
                               errónea, correcta, rectificados);

        return Ok(new
        {
            ok = true,
            mensaje = $"Matrícula corregida a {correcta} en {rectificados} registro(s). " +
                      "El sistema aplicará esta corrección automáticamente en las próximas lecturas.",
            matricula = correcta,
        });
    }

    /// <summary>Correcciones aprendidas, para poder revisarlas o retirarlas.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = await db.PlateCorrections.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.Id, leido = c.WrongText, correcto = c.CorrectText, veces = c.TimesApplied, c.CreatedAt })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Olvidar(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PlateCorrections.FindAsync(new object[] { id }, ct);
        if (item is not null)
        {
            db.PlateCorrections.Remove(item);
            await db.SaveChangesAsync(ct);
            _index.MarkDirty();
            await _index.RefreshAsync(ct);
        }

        return Ok(new { ok = true });
    }
}
