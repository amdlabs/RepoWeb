using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

/// <summary>
/// Catálogo de objetos: las clases detectadas sin etiquetar se listan como
/// «desconocidos» y, al asignarles una etiqueta, pasan a «conocidos». Las
/// detecciones futuras de esa clase salen ya identificadas.
/// </summary>
public class ObjetosModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly KnownSubjectsIndex _index;

    public ObjetosModel(IDbContextFactory<VisionDbContext> dbFactory, KnownSubjectsIndex index)
    {
        _dbFactory = dbFactory;
        _index = index;
    }

    public sealed record UnknownClass(string ClassName, int Detections, DateTime LastSeen, string? LastCropPath,
                                      string? LastFullFramePath);

    public IReadOnlyList<ObjectLabel> Labels { get; private set; } = Array.Empty<ObjectLabel>();
    public IReadOnlyList<UnknownClass> Unlabeled { get; private set; } = Array.Empty<UnknownClass>();
    public string? DatabaseError { get; private set; }

    private const int PageSize = 15;

    [BindProperty(SupportsGet = true)] public int Pagina { get; set; } = 1;

    /// <summary>Filtro por cámara de origen de las detecciones.</summary>
    [BindProperty(SupportsGet = true)] public string? Camara { get; set; }

    public int TotalPaginas { get; private set; } = 1;
    public int UnlabeledTotal { get; private set; }
    public IReadOnlyList<string> CamerasDisponibles { get; private set; } = Array.Empty<string>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            Labels = await db.ObjectLabels
                .AsNoTracking()
                .OrderBy(o => o.DisplayName)
                .ToListAsync(ct);

            var labeled = Labels.Select(l => l.ClassName.ToLowerInvariant()).ToHashSet();

            var events = db.RecognitionEvents
                .AsNoTracking()
                .Where(e => e.Kind == RecognitionKind.Object && e.ObjectClass != null);

            CamerasDisponibles = await events.Select(e => e.CameraName).Distinct().OrderBy(n => n).ToListAsync(ct);

            if (!string.IsNullOrWhiteSpace(Camara))
                events = events.Where(e => e.CameraName == Camara);

            var detected = await events
                .GroupBy(e => e.ObjectClass!)
                .Select(g => new
                {
                    ClassName = g.Key,
                    Detections = g.Count(),
                    LastSeen = g.Max(e => e.OccurredAt),
                    LastCropPath = g.OrderByDescending(e => e.OccurredAt)
                                    .Select(e => e.CropPath)
                                    .FirstOrDefault(),
                    LastFullFramePath = g.OrderByDescending(e => e.OccurredAt)
                                    .Select(e => e.FullFramePath)
                                    .FirstOrDefault(),
                })
                .ToListAsync(ct);

            // Las personas no se etiquetan como objetos: se gestionan en la página
            // Personas (rostros detectados sin identificar, con alta directa).
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "persona", "person" };

            var all = detected
                .Where(d => !labeled.Contains(d.ClassName.ToLowerInvariant()) && !excluded.Contains(d.ClassName))
                .OrderByDescending(d => d.LastSeen)
                .Select(d => new UnknownClass(d.ClassName, d.Detections, d.LastSeen, d.LastCropPath, d.LastFullFramePath))
                .ToList();

            UnlabeledTotal = all.Count;
            TotalPaginas = Math.Max(1, (int)Math.Ceiling(UnlabeledTotal / (double)PageSize));
            Pagina = Math.Clamp(Pagina, 1, TotalPaginas);

            Unlabeled = all.Skip((Pagina - 1) * PageSize).Take(PageSize).ToList();
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }

    /// <summary>Borra las detecciones de objetos (no toca las etiquetas ya creadas).</summary>
    public async Task<IActionResult> OnPostLimpiarDeteccionesAsync(
        [FromServices] Services.DetectionCleanup cleanup,
        int? dias, bool soloSinEtiquetar, string? camara, CancellationToken ct)
    {
        if (!Services.RoleGuard.CanEdit(User)) return Forbid();

        // «Sin etiquetar» = clases que no tienen etiqueta de usuario todavía.
        IReadOnlyCollection<string>? clases = null;
        if (soloSinEtiquetar)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var etiquetadas = await db.ObjectLabels.AsNoTracking().Select(o => o.ClassName).ToListAsync(ct);

            clases = await db.RecognitionEvents.AsNoTracking()
                .Where(e => e.Kind == RecognitionKind.Object && e.ObjectClass != null)
                .Select(e => e.ObjectClass!)
                .Distinct()
                .ToListAsync(ct);

            clases = clases.Where(c => !etiquetadas.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

            if (clases.Count == 0)
            {
                TempData["Ok"] = "No hay detecciones sin etiquetar que borrar.";
                return RedirectToPage(new { Camara = camara });
            }
        }

        var (eventos, imagenes) = await cleanup.DeleteAsync(
            kinds: new[] { RecognitionKind.Object, RecognitionKind.Text },
            objectClasses: clases,
            camera: camara,
            days: dias,
            ct: ct);

        TempData["Ok"] = $"Se han borrado {eventos} detección(es) de objetos/textos y {imagenes} imagen(es). " +
                          "Las etiquetas creadas se mantienen.";
        return RedirectToPage(new { Camara = camara });
    }

    /// <summary>Da nombre a una clase detectada (o actualiza la etiqueta existente).</summary>
    public async Task<IActionResult> OnPostEtiquetarAsync(string className, string displayName,
                                                          bool isAuthorized, string? notes, CancellationToken ct)
    {
        if (!Services.RoleGuard.CanEdit(User)) return Forbid();

        className = (className ?? "").Trim();
        displayName = (displayName ?? "").Trim();

        if (className.Length == 0 || displayName.Length == 0)
        {
            TempData["Error"] = "Hay que indicar la clase y el nombre de la etiqueta.";
            return RedirectToPage();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.ObjectLabels.FirstOrDefaultAsync(o => o.ClassName == className, ct);
        if (existing is null)
        {
            db.ObjectLabels.Add(new ObjectLabel
            {
                ClassName = className,
                DisplayName = displayName,
                IsAuthorized = isAuthorized,
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            });
        }
        else
        {
            existing.DisplayName = displayName;
            existing.IsAuthorized = isAuthorized;
            existing.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            existing.IsActive = true;
        }

        await db.SaveChangesAsync(ct);
        _index.MarkDirty();

        TempData["Ok"] = $"Objeto «{displayName}» etiquetado. Las próximas detecciones de «{className}» saldrán como conocidas.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEliminarAsync(int id, CancellationToken ct)
    {
        if (!Services.RoleGuard.CanEdit(User)) return Forbid();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var label = await db.ObjectLabels.FindAsync(new object[] { id }, ct);
        if (label is not null)
        {
            db.ObjectLabels.Remove(label);
            await db.SaveChangesAsync(ct);
            _index.MarkDirty();
            TempData["Ok"] = $"Etiqueta «{label.DisplayName}» eliminada; la clase «{label.ClassName}» vuelve a listar como desconocida.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAlternarAsync(int id, CancellationToken ct)
    {
        if (!Services.RoleGuard.CanEdit(User)) return Forbid();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var label = await db.ObjectLabels.FindAsync(new object[] { id }, ct);
        if (label is not null)
        {
            label.IsAuthorized = !label.IsAuthorized;
            await db.SaveChangesAsync(ct);
            _index.MarkDirty();
        }

        return RedirectToPage();
    }
}
