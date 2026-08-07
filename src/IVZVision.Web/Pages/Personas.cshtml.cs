using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

public class PersonasModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly KnownSubjectsIndex _index;

    public PersonasModel(IDbContextFactory<VisionDbContext> dbFactory, KnownSubjectsIndex index)
    {
        _dbFactory = dbFactory;
        _index = index;
    }

    public sealed record PersonRow(int Id, string FullName, string? DocumentId, string? Department,
                                   bool IsAuthorized, bool IsActive, int Templates, DateTime CreatedAt);

    public IReadOnlyList<PersonRow> People { get; private set; } = Array.Empty<PersonRow>();

    [BindProperty] public Person NewPerson { get; set; } = new();

    [BindProperty(SupportsGet = true)] public string? Buscar { get; set; }

    public string? DatabaseError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostCrearAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(NewPerson.FullName))
        {
            TempData["Error"] = "El nombre es obligatorio.";
            return RedirectToPage();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var person = new Person
        {
            FullName = NewPerson.FullName.Trim(),
            DocumentId = string.IsNullOrWhiteSpace(NewPerson.DocumentId) ? null : NewPerson.DocumentId.Trim(),
            Department = string.IsNullOrWhiteSpace(NewPerson.Department) ? null : NewPerson.Department.Trim(),
            IsAuthorized = NewPerson.IsAuthorized,
            Notes = NewPerson.Notes,
        };

        db.Persons.Add(person);
        await db.SaveChangesAsync(ct);
        _index.MarkDirty();

        TempData["Ok"] = $"Persona «{person.FullName}» creada. Añada ahora una o varias fotos para registrar su rostro.";
        return RedirectToPage("/Persona", new { id = person.Id });
    }

    public async Task<IActionResult> OnPostEliminarAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var person = await db.Persons.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (person is null)
        {
            TempData["Error"] = "La persona ya no existe.";
            return RedirectToPage();
        }

        db.Persons.Remove(person);
        await db.SaveChangesAsync(ct);
        _index.MarkDirty();
        await _index.RefreshAsync(ct);

        TempData["Ok"] = $"Persona «{person.FullName}» eliminada junto con sus plantillas faciales.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var query = db.Persons.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(Buscar))
            {
                var term = Buscar.Trim();
                query = query.Where(p => p.FullName.Contains(term)
                                      || (p.DocumentId != null && p.DocumentId.Contains(term))
                                      || (p.Department != null && p.Department.Contains(term)));
            }

            People = await query
                .OrderBy(p => p.FullName)
                .Select(p => new PersonRow(p.Id, p.FullName, p.DocumentId, p.Department,
                                           p.IsAuthorized, p.IsActive, p.FaceTemplates.Count, p.CreatedAt))
                .Take(500)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }
}
