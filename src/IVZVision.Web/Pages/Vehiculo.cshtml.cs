using IVZVision.Core.Util;
using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

public class VehiculoModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly KnownSubjectsIndex _index;

    public VehiculoModel(IDbContextFactory<VisionDbContext> dbFactory, KnownSubjectsIndex index)
    {
        _dbFactory = dbFactory;
        _index = index;
    }

    [BindProperty] public Vehicle Vehicle { get; set; } = new();

    public IReadOnlyList<SelectListItem> Owners { get; private set; } = Array.Empty<SelectListItem>();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is null)
        {
            TempData["Error"] = "El vehículo solicitado no existe.";
            return RedirectToPage("/Vehiculos");
        }

        Vehicle = vehicle;
        Owners = await LoadOwnersAsync(db, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == Vehicle.Id, ct);
        if (vehicle is null)
        {
            TempData["Error"] = "El vehículo ya no existe.";
            return RedirectToPage("/Vehiculos");
        }

        var normalized = PlateText.Normalize(Vehicle.Plate);
        if (normalized.Length < 2)
        {
            TempData["Error"] = "Indique una matrícula válida.";
            Owners = await LoadOwnersAsync(db, ct);
            return Page();
        }

        if (normalized != vehicle.Plate &&
            await db.Vehicles.AnyAsync(v => v.Plate == normalized && v.Id != vehicle.Id, ct))
        {
            TempData["Error"] = $"La matrícula {normalized} ya está registrada en otro vehículo.";
            Owners = await LoadOwnersAsync(db, ct);
            return Page();
        }

        vehicle.Plate = normalized;
        vehicle.PlateRaw = Vehicle.Plate?.Trim();
        vehicle.Make = Clean(Vehicle.Make);
        vehicle.Model = Clean(Vehicle.Model);
        vehicle.Color = Clean(Vehicle.Color);
        vehicle.OwnerPersonId = Vehicle.OwnerPersonId == 0 ? null : Vehicle.OwnerPersonId;
        vehicle.IsAuthorized = Vehicle.IsAuthorized;
        vehicle.IsActive = Vehicle.IsActive;
        vehicle.Notes = Vehicle.Notes;
        vehicle.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        _index.MarkDirty();
        await _index.RefreshAsync(ct);

        TempData["Ok"] = $"Vehículo {vehicle.Plate} actualizado.";
        return RedirectToPage("/Vehiculos");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<IReadOnlyList<SelectListItem>> LoadOwnersAsync(VisionDbContext db, CancellationToken ct)
        => await db.Persons.AsNoTracking()
            .OrderBy(p => p.FullName)
            .Select(p => new SelectListItem(p.FullName, p.Id.ToString()))
            .Take(500)
            .ToListAsync(ct);
}
