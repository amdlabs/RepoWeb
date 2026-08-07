using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Services;

public sealed record DashboardVehicle(string Matricula, string Etiqueta, bool Registrado, int VecesVisto,
                                      DateTime PrimeraVez, DateTime UltimaVez, string? UltimaCamara,
                                      bool YaVistoAntes);

public sealed record DashboardSummary(
    int VehiculosHoy,
    int VehiculosNuevosHoy,
    int VehiculosRecurrentesHoy,
    int VehiculosTotal,
    int RostrosHoy,
    int ObjetosHoy,
    int EventosHoy,
    IReadOnlyList<DashboardVehicle> UltimosVehiculos);

/// <summary>
/// Resumen del dashboard: vehículos vistos (con memoria de si ya se vieron antes)
/// y contadores del día. Lo consumen la página y el endpoint JSON de tiempo real.
/// </summary>
public sealed class DashboardService
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;

    public DashboardService(IDbContextFactory<VisionDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Los eventos se guardan en UTC; «hoy» es el día local.
        var todayUtc = DateTime.Today.ToUniversalTime();

        var plates = db.RecognitionEvents
            .AsNoTracking()
            .Where(e => e.Kind == RecognitionKind.Plate && e.PlateText != null && e.PlateText != "");

        // Resumen por matrícula: cuántas veces se ha visto, primera y última vez.
        var perPlate = await plates
            .GroupBy(e => e.PlateText!)
            .Select(g => new
            {
                Plate = g.Key,
                TimesSeen = g.Count(),
                FirstSeen = g.Min(e => e.OccurredAt),
                LastSeen = g.Max(e => e.OccurredAt),
            })
            .ToListAsync(ct);

        var seenToday = perPlate.Where(p => p.LastSeen >= todayUtc).ToList();

        var facesToday = await db.RecognitionEvents.AsNoTracking()
            .CountAsync(e => e.Kind == RecognitionKind.Face && e.OccurredAt >= todayUtc, ct);
        var objectsToday = await db.RecognitionEvents.AsNoTracking()
            .CountAsync(e => e.Kind == RecognitionKind.Object && e.OccurredAt >= todayUtc, ct);
        var eventsToday = await db.RecognitionEvents.AsNoTracking()
            .CountAsync(e => e.OccurredAt >= todayUtc, ct);

        // Últimos 30 vehículos con el detalle de su última lectura.
        var recent = perPlate.OrderByDescending(p => p.LastSeen).Take(30).ToList();
        var keys = recent.Select(p => p.Plate).ToList();

        var lastEvents = await plates
            .Where(e => keys.Contains(e.PlateText!))
            .GroupBy(e => e.PlateText!)
            .Select(g => g.OrderByDescending(e => e.OccurredAt)
                          .Select(e => new { e.PlateText, e.Label, e.IsKnown, e.CameraName })
                          .First())
            .ToListAsync(ct);

        var vehicles = recent.Select(p =>
        {
            var last = lastEvents.FirstOrDefault(e => e.PlateText == p.Plate);
            return new DashboardVehicle(
                p.Plate,
                last?.Label ?? p.Plate,
                last?.IsKnown ?? false,
                p.TimesSeen,
                p.FirstSeen,
                p.LastSeen,
                last?.CameraName,
                YaVistoAntes: p.TimesSeen > 1);
        }).ToList();

        return new DashboardSummary(
            VehiculosHoy: seenToday.Count,
            VehiculosNuevosHoy: seenToday.Count(p => p.FirstSeen >= todayUtc),
            VehiculosRecurrentesHoy: seenToday.Count(p => p.FirstSeen < todayUtc),
            VehiculosTotal: perPlate.Count,
            RostrosHoy: facesToday,
            ObjetosHoy: objectsToday,
            EventosHoy: eventsToday,
            UltimosVehiculos: vehicles);
    }
}
