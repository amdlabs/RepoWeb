using IVZVision.Core.Util;
using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

public class VehiculosModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly KnownSubjectsIndex _index;

    public VehiculosModel(IDbContextFactory<VisionDbContext> dbFactory, KnownSubjectsIndex index)
    {
        _dbFactory = dbFactory;
        _index = index;
    }

    public sealed record VehicleRow(int Id, string Plate, string? PlateRaw, string Description,
                                    string? Owner, bool IsAuthorized, bool IsActive);

    public IReadOnlyList<VehicleRow> Vehicles { get; private set; } = Array.Empty<VehicleRow>();
    public IReadOnlyList<SelectListItem> Owners { get; private set; } = Array.Empty<SelectListItem>();
    public string? DatabaseError { get; private set; }

    [BindProperty] public Vehicle NewVehicle { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? Buscar { get; set; }

    // ---- Grilla de vehículos detectados por las cámaras ----------------------

    public sealed record DetectedVehicle(long EventId, DateTime OccurredAt, string CameraName,
                                         string Plate, bool IsKnown, string? CropBase64, string? CropPath,
                                         int Repeticiones);

    public IReadOnlyList<DetectedVehicle> Detected { get; private set; } = Array.Empty<DetectedVehicle>();
    public int DetectedTotal { get; private set; }
    public int TotalPaginas { get; private set; } = 1;
    public IReadOnlyList<string> CamerasDisponibles { get; private set; } = Array.Empty<string>();

    private const int DetectionsPageSize = 20;

    [BindProperty(SupportsGet = true)] public int Pagina { get; set; } = 1;

    /// <summary>Filtro por cámara de origen de la lectura.</summary>
    [BindProperty(SupportsGet = true)] public string? Camara { get; set; }

    private async Task LoadDetectedAsync(VisionDbContext db, CancellationToken ct)
    {
        var query = db.RecognitionEvents
            .AsNoTracking()
            .Where(e => e.Kind == RecognitionKind.Plate && e.PlateText != null && e.PlateText != "");

        CamerasDisponibles = await query.Select(e => e.CameraName).Distinct().OrderBy(n => n).ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(Camara))
            query = query.Where(e => e.CameraName == Camara);

        // Lecturas repetidas de la misma matrícula en la misma cámara (10 min) → una fila con ×N.
        var raw = await query
            .OrderByDescending(e => e.OccurredAt)
            .Take(600)
            .Select(e => new { e.Id, e.OccurredAt, e.CameraName, e.PlateText, e.IsKnown, e.CropBase64, e.CropPath })
            .ToListAsync(ct);

        var grouped = new List<DetectedVehicle>();
        foreach (var e in raw)
        {
            var previous = grouped.FindIndex(g =>
                g.CameraName == e.CameraName
                && g.Plate == e.PlateText
                && (g.OccurredAt - e.OccurredAt) < TimeSpan.FromMinutes(10));

            if (previous >= 0)
            {
                grouped[previous] = grouped[previous] with { Repeticiones = grouped[previous].Repeticiones + 1 };
                continue;
            }

            grouped.Add(new DetectedVehicle(e.Id, e.OccurredAt, e.CameraName, e.PlateText!, e.IsKnown,
                                            e.CropBase64, e.CropPath, 1));
        }

        DetectedTotal = grouped.Count;
        TotalPaginas = Math.Max(1, (int)Math.Ceiling(DetectedTotal / (double)DetectionsPageSize));
        Pagina = Math.Clamp(Pagina, 1, TotalPaginas);

        Detected = grouped.Skip((Pagina - 1) * DetectionsPageSize).Take(DetectionsPageSize).ToList();
    }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCrearAsync(CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        var normalized = PlateText.Normalize(NewVehicle.Plate);

        if (normalized.Length < 2)
        {
            TempData["Error"] = "Indique una matrícula válida.";
            return RedirectToPage();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (await db.Vehicles.AnyAsync(v => v.Plate == normalized, ct))
        {
            TempData["Error"] = $"La matrícula {normalized} ya está registrada.";
            return RedirectToPage();
        }

        db.Vehicles.Add(new Vehicle
        {
            Plate = normalized,
            PlateRaw = NewVehicle.Plate?.Trim(),
            Make = Clean(NewVehicle.Make),
            Model = Clean(NewVehicle.Model),
            Color = Clean(NewVehicle.Color),
            OwnerPersonId = NewVehicle.OwnerPersonId == 0 ? null : NewVehicle.OwnerPersonId,
            IsAuthorized = NewVehicle.IsAuthorized,
            Notes = NewVehicle.Notes,
        });

        await db.SaveChangesAsync(ct);
        _index.MarkDirty();
        await _index.RefreshAsync(ct);

        TempData["Ok"] = $"Vehículo {normalized} registrado.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEliminarAsync(int id, CancellationToken ct)
    {
        if (!RoleGuard.CanEdit(User)) return Forbid();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is not null)
        {
            db.Vehicles.Remove(vehicle);
            await db.SaveChangesAsync(ct);
            _index.MarkDirty();
            await _index.RefreshAsync(ct);
            TempData["Ok"] = $"Vehículo {vehicle.Plate} eliminado.";
        }

        return RedirectToPage();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var query = db.Vehicles.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(Buscar))
            {
                var term = PlateText.Normalize(Buscar);
                var raw = Buscar.Trim();
                query = query.Where(v => v.Plate.Contains(term)
                                      || (v.Make != null && v.Make.Contains(raw))
                                      || (v.Model != null && v.Model.Contains(raw)));
            }

            Vehicles = await query
                .OrderBy(v => v.Plate)
                .Select(v => new VehicleRow(
                    v.Id, v.Plate, v.PlateRaw,
                    ((v.Make ?? "") + " " + (v.Model ?? "") + " " + (v.Color ?? "")).Trim(),
                    v.OwnerPerson != null ? v.OwnerPerson.FullName : null,
                    v.IsAuthorized, v.IsActive))
                .Take(500)
                .ToListAsync(ct);

            Owners = await db.Persons.AsNoTracking()
                .OrderBy(p => p.FullName)
                .Select(p => new SelectListItem(p.FullName, p.Id.ToString()))
                .Take(500)
                .ToListAsync(ct);

            await LoadDetectedAsync(db, ct);
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }
}
