using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

public class PersonaModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly EnrollmentService _enrollment;
    private readonly KnownSubjectsIndex _index;

    public PersonaModel(IDbContextFactory<VisionDbContext> dbFactory, EnrollmentService enrollment,
                        KnownSubjectsIndex index)
    {
        _dbFactory = dbFactory;
        _enrollment = enrollment;
        _index = index;
    }

    public sealed record TemplateRow(int Id, string? ImagePath, string ModelId, int Dimensions,
                                     float Quality, DateTime CreatedAt);

    [BindProperty] public Person Person { get; set; } = new();

    public IReadOnlyList<TemplateRow> Templates { get; private set; } = Array.Empty<TemplateRow>();

    public IReadOnlyList<Vehicle> Vehicles { get; private set; } = Array.Empty<Vehicle>();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct);
        return loaded ? Page() : RedirectToPage("/Personas");
    }

    public async Task<IActionResult> OnPostGuardarAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var person = await db.Persons.FirstOrDefaultAsync(p => p.Id == Person.Id, ct);
        if (person is null)
        {
            TempData["Error"] = "La persona ya no existe.";
            return RedirectToPage("/Personas");
        }

        person.FullName = Person.FullName.Trim();
        person.DocumentId = string.IsNullOrWhiteSpace(Person.DocumentId) ? null : Person.DocumentId.Trim();
        person.Department = string.IsNullOrWhiteSpace(Person.Department) ? null : Person.Department.Trim();
        person.IsAuthorized = Person.IsAuthorized;
        person.IsActive = Person.IsActive;
        person.Notes = Person.Notes;
        person.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        _index.MarkDirty();
        await _index.RefreshAsync(ct);

        TempData["Ok"] = "Datos actualizados.";
        return RedirectToPage(new { id = person.Id });
    }

    public async Task<IActionResult> OnPostSubirAsync(int id, List<IFormFile> fotos, CancellationToken ct)
    {
        if (fotos is null || fotos.Count == 0)
        {
            TempData["Error"] = "Seleccione al menos una imagen.";
            return RedirectToPage(new { id });
        }

        var ok = new List<string>();
        var failed = new List<string>();

        foreach (var file in fotos)
        {
            var result = await _enrollment.EnrollAsync(id, file, ct);
            if (result.Success) ok.Add(file.FileName);
            else failed.Add($"{file.FileName}: {result.Message}");
        }

        if (ok.Count > 0)
            TempData["Ok"] = $"{ok.Count} rostro(s) registrado(s): {string.Join(", ", ok)}.";

        if (failed.Count > 0)
            TempData["Error"] = string.Join(" · ", failed);

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostBorrarPlantillaAsync(int id, int templateId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var template = await db.FaceTemplates.FirstOrDefaultAsync(t => t.Id == templateId && t.PersonId == id, ct);
        if (template is not null)
        {
            db.FaceTemplates.Remove(template);
            await db.SaveChangesAsync(ct);
            _index.MarkDirty();
            await _index.RefreshAsync(ct);
            TempData["Ok"] = "Plantilla eliminada.";
        }

        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var person = await db.Persons.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (person is null)
        {
            TempData["Error"] = "La persona solicitada no existe.";
            return false;
        }

        Person = person;

        Templates = await db.FaceTemplates.AsNoTracking()
            .Where(t => t.PersonId == id)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TemplateRow(t.Id, t.ImagePath, t.ModelId, t.Dimensions, t.Quality, t.CreatedAt))
            .ToListAsync(ct);

        Vehicles = await db.Vehicles.AsNoTracking()
            .Where(v => v.OwnerPersonId == id)
            .OrderBy(v => v.Plate)
            .ToListAsync(ct);

        return true;
    }
}
