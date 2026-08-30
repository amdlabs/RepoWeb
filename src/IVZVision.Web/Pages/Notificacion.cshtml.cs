using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

/// <summary>
/// Destino de los avisos push: al tocar la notificación en el teléfono se abre esta
/// página con la foto completa del aviso y el vídeo en vivo de esa cámara.
/// </summary>
public class NotificacionModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;

    public NotificacionModel(IDbContextFactory<VisionDbContext> dbFactory) => _dbFactory = dbFactory;

    /// <summary>Id del evento, tal como viene en la dirección del aviso.</summary>
    [BindProperty(SupportsGet = true)] public long Evento { get; set; }

    /// <summary>El registro del aviso, o null si ya no existe.</summary>
    public RecognitionEvent? Registro { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        Registro = await db.RecognitionEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == Evento, ct);
    }
}
