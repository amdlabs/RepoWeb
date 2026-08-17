using IVZVision.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IVZVision.Web.Controllers;

/// <summary>Resumen del dashboard para el refresco en tiempo real (requiere sesión).</summary>
[ApiController]
[Authorize]
[Route("api/dashboard")]
public class DashboardApiController : ControllerBase
{
    private readonly DashboardService _dashboard;

    public DashboardApiController(DashboardService dashboard) => _dashboard = dashboard;

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen(CancellationToken ct) =>
        Ok(await _dashboard.GetSummaryAsync(ct));
}
