using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

public class EventosModel : PageModel
{
    private const int PageSize = 50;

    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly IConfigStore _config;

    public EventosModel(IDbContextFactory<VisionDbContext> dbFactory, IConfigStore config)
    {
        _dbFactory = dbFactory;
        _config = config;
    }

    public sealed record EventRow(long Id, DateTime OccurredAt, string CameraName, RecognitionKind Kind,
                                  RecognitionSource Source, string Label, string? PlateText, bool IsKnown,
                                  bool IsAuthorized, float MatchScore, float DetectionScore,
                                  float? OcrConfidence, string? CropPath, int? PersonId, int? VehicleId);

    public IReadOnlyList<EventRow> Events { get; private set; } = Array.Empty<EventRow>();
    public IReadOnlyList<SelectListItem> Cameras { get; private set; } = Array.Empty<SelectListItem>();
    public string? DatabaseError { get; private set; }

    public int TotalPages { get; private set; }
    public int Total { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Tipo { get; set; }
    [BindProperty(SupportsGet = true)] public string? Camara { get; set; }
    [BindProperty(SupportsGet = true)] public string? Estado { get; set; }
    [BindProperty(SupportsGet = true)] public string? Texto { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? Desde { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? Hasta { get; set; }
    [BindProperty(SupportsGet = true)] public int Pagina { get; set; } = 1;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Cameras = _config.Current.Cameras
            .Select(c => new SelectListItem(c.Name, c.Id.ToString()))
            .ToList();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var query = db.RecognitionEvents.AsNoTracking().AsQueryable();

            if (Tipo == "rostro") query = query.Where(e => e.Kind == RecognitionKind.Face);
            else if (Tipo == "matricula") query = query.Where(e => e.Kind == RecognitionKind.Plate);

            if (Guid.TryParse(Camara, out var cameraId)) query = query.Where(e => e.CameraId == cameraId);

            if (Estado == "conocido") query = query.Where(e => e.IsKnown);
            else if (Estado == "desconocido") query = query.Where(e => !e.IsKnown);
            else if (Estado == "noautorizado") query = query.Where(e => e.IsKnown && !e.IsAuthorized);

            if (!string.IsNullOrWhiteSpace(Texto))
            {
                var term = Texto.Trim();
                query = query.Where(e => e.Label.Contains(term)
                                      || (e.PlateText != null && e.PlateText.Contains(term)));
            }

            // Los eventos se guardan en UTC; los filtros vienen en hora local.
            if (Desde.HasValue) query = query.Where(e => e.OccurredAt >= Desde.Value.ToUniversalTime());
            if (Hasta.HasValue) query = query.Where(e => e.OccurredAt < Hasta.Value.AddDays(1).ToUniversalTime());

            Total = await query.CountAsync(ct);
            TotalPages = Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
            Pagina = Math.Clamp(Pagina, 1, TotalPages);

            Events = await query
                .OrderByDescending(e => e.OccurredAt)
                .Skip((Pagina - 1) * PageSize)
                .Take(PageSize)
                .Select(e => new EventRow(e.Id, e.OccurredAt, e.CameraName, e.Kind, e.Source, e.Label,
                                          e.PlateText, e.IsKnown, e.IsAuthorized, e.MatchScore,
                                          e.DetectionScore, e.OcrConfidence, e.CropPath, e.PersonId, e.VehicleId))
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }
}
