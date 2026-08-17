using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IVZVision.Web.Pages;

/// <summary>
/// Panel de resumen en tiempo real: vehículos vistos (nuevos frente a ya vistos
/// antes) y contadores del día. El refresco en vivo lo hace dashboard.js vía
/// SignalR + /api/dashboard/resumen.
/// </summary>
public class DashboardModel : PageModel
{
    private readonly DashboardService _dashboard;

    public DashboardModel(DashboardService dashboard) => _dashboard = dashboard;

    public DashboardSummary Summary { get; private set; } =
        new(0, 0, 0, 0, 0, 0, 0, Array.Empty<DashboardVehicle>());

    public string? DatabaseError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Summary = await _dashboard.GetSummaryAsync(ct);
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }
}
