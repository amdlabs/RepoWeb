using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

public class PendientesModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly PendingSubjectService _pending;

    public PendientesModel(IDbContextFactory<VisionDbContext> dbFactory, PendingSubjectService pending)
    {
        _dbFactory = dbFactory;
        _pending = pending;
    }

    public sealed record PendingRow(long Id, RecognitionKind Kind, string CameraName, DateTime FirstSeenAt,
                                    DateTime LastSeenAt, int Occurrences, string? PlateText, string? ObjectClass,
                                    float BestScore, string? CropPath, bool CanLearn);

    public IReadOnlyList<PendingRow> Items { get; private set; } = Array.Empty<PendingRow>();
    public IReadOnlyList<SelectListItem> People { get; private set; } = Array.Empty<SelectListItem>();
    public IReadOnlyList<SelectListItem> Objects { get; private set; } = Array.Empty<SelectListItem>();
    public string? DatabaseError { get; private set; }

    public int TotalFaces { get; private set; }
    public int TotalPlates { get; private set; }
    public int TotalObjects { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Tipo { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAsignarRostroAsync(long id, int personaId, string? nombre,
                                                              bool autorizado, CancellationToken ct)
    {
        var result = await _pending.AssignFaceAsync(id, personaId > 0 ? personaId : null, nombre, autorizado, ct);
        return Feedback(result.Success, result.Message);
    }

    public async Task<IActionResult> OnPostAsignarMatriculaAsync(long id, string? descripcion,
                                                                 bool autorizado, CancellationToken ct)
    {
        var result = await _pending.AssignPlateAsync(id, null, descripcion, null, autorizado, ct);
        return Feedback(result.Success, result.Message);
    }

    public async Task<IActionResult> OnPostAsignarObjetoAsync(long id, int objetoId, string? nombre,
                                                              bool autorizado, CancellationToken ct)
    {
        var result = await _pending.AssignObjectAsync(id, objetoId > 0 ? objetoId : null, nombre, autorizado, ct);
        return Feedback(result.Success, result.Message);
    }

    public async Task<IActionResult> OnPostIgnorarAsync(long id, CancellationToken ct)
    {
        var ok = await _pending.SetStatusAsync(id, PendingStatus.Ignored, ct);
        return Feedback(ok, ok ? "Ficha descartada." : "La ficha ya no existe.");
    }

    public async Task<IActionResult> OnPostEliminarAsync(long id, CancellationToken ct)
    {
        var ok = await _pending.DeleteAsync(id, ct);
        return Feedback(ok, ok ? "Ficha eliminada." : "La ficha ya no existe.");
    }

    private IActionResult Feedback(bool success, string message)
    {
        if (success) TempData["Ok"] = message;
        else TempData["Error"] = message;

        return RedirectToPage(new { Tipo });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var pending = db.PendingSubjects.AsNoTracking().Where(p => p.Status == PendingStatus.Pending);

            TotalFaces = await pending.CountAsync(p => p.Kind == RecognitionKind.Face, ct);
            TotalPlates = await pending.CountAsync(p => p.Kind == RecognitionKind.Plate, ct);
            TotalObjects = await pending.CountAsync(p => p.Kind == RecognitionKind.Object, ct);

            if (Enum.TryParse<RecognitionKind>(Tipo, ignoreCase: true, out var kind))
                pending = pending.Where(p => p.Kind == kind);

            Items = await pending
                .OrderByDescending(p => p.Occurrences).ThenByDescending(p => p.LastSeenAt)
                .Take(200)
                .Select(p => new PendingRow(p.Id, p.Kind, p.CameraName, p.FirstSeenAt, p.LastSeenAt,
                                            p.Occurrences, p.PlateText, p.ObjectClass, p.BestScore, p.CropPath,
                                            p.Embedding != null || p.PlateText != null))
                .ToListAsync(ct);

            People = await db.Persons.AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.FullName)
                .Select(p => new SelectListItem(p.FullName, p.Id.ToString()))
                .Take(500)
                .ToListAsync(ct);

            Objects = await db.KnownObjects.AsNoTracking()
                .Where(o => o.IsActive)
                .OrderBy(o => o.Name)
                .Select(o => new SelectListItem(o.Name, o.Id.ToString()))
                .Take(500)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }
}
