using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Data.Search;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Controllers;

/// <summary>
/// API de consulta. Todas las rutas exigen un token de uso, que se crea en
/// Configuración → API. Se admite <c>X-API-Token</c>, <c>Authorization: Bearer</c>
/// o <c>?token=</c>.
/// </summary>
[ApiController]
[Route("api")]
[Produces("application/json")]
public class VerController : ControllerBase
{
    private readonly LiveViewService _live;
    private readonly ApiTokenValidator _tokens;
    private readonly IConfigStore _config;
    private readonly SearchService _search;
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;

    public VerController(LiveViewService live, ApiTokenValidator tokens, IConfigStore config,
                         SearchService search, IDbContextFactory<VisionDbContext> dbFactory)
    {
        _live = live;
        _tokens = tokens;
        _config = config;
        _search = search;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Devuelve lo que las cámaras están viendo en este mismo instante: rostros,
    /// matrículas, objetos, códigos, texto y alertas de actividad.
    /// </summary>
    /// <param name="camara">Id de una cámara concreta (opcional).</param>
    /// <param name="tipo">Filtra por tipo: rostro, matricula, objeto, codigo o texto.</param>
    /// <param name="imagen">Incluye el fotograma anotado en base64.</param>
    [HttpGet("ver")]
    [HttpPost("ver")]
    public IActionResult Ver([FromQuery] Guid? camara, [FromQuery] string? tipo, [FromQuery] bool imagen = false)
    {
        if (!_config.Current.Api.RestEnabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "La API REST está desactivada en la configuración." });

        var check = _tokens.Validate(Request, requireImages: imagen);
        if (!check.Ok) return Unauthorized(new { error = check.Error });

        return Ok(_live.Build(camara, imagen, tipo));
    }

    /// <summary>Lista las cámaras configuradas y su estado.</summary>
    [HttpGet("camaras")]
    public IActionResult Camaras()
    {
        var check = _tokens.Validate(Request);
        if (!check.Ok) return Unauthorized(new { error = check.Error });

        var vista = _live.Build();

        return Ok(vista.detalle.Select(c => new
        {
            c.id, c.nombre, c.origen, c.conectada, c.estado, c.fps,
            resolucion = c.ancho > 0 ? $"{c.ancho}x{c.alto}" : null,
            objetos = c.viendo.Count,
            alertas = c.alertas.Count,
        }));
    }

    /// <summary>
    /// Busca en el histórico y en la lista de desconocidos a partir de una frase
    /// en castellano. Ejemplos: «personas desconocidas de anoche en la entrada»,
    /// «matrículas de las últimas 2 horas», «alertas de animales de esta semana».
    /// </summary>
    [HttpGet("buscar")]
    [HttpPost("buscar")]
    public async Task<IActionResult> Buscar([FromQuery] string? prompt, [FromQuery] int limite = 50,
                                            CancellationToken ct = default)
    {
        var check = _tokens.Validate(Request);
        if (!check.Ok) return Unauthorized(new { error = check.Error });

        try
        {
            var result = await _search.SearchAsync(prompt, limite, includePending: true, ct);

            return Ok(new
            {
                consulta = prompt,
                interpretacion = result.Interpretation,
                total = result.Total,
                resultados = result.Hits,
            });
        }
        catch (Exception ex)
        {
            return DatabaseUnavailable(ex);
        }
    }

    /// <summary>Sujetos detectados que el sistema no ha sabido identificar y esperan un nombre.</summary>
    [HttpGet("pendientes")]
    public async Task<IActionResult> Pendientes([FromQuery] string? tipo, [FromQuery] int limite = 50,
                                                CancellationToken ct = default)
    {
        var check = _tokens.Validate(Request);
        if (!check.Ok) return Unauthorized(new { error = check.Error });

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var query = db.PendingSubjects.AsNoTracking().Where(p => p.Status == PendingStatus.Pending);

            if (Enum.TryParse<RecognitionKind>(tipo, ignoreCase: true, out var kind))
                query = query.Where(p => p.Kind == kind);

            var items = await query
                .OrderByDescending(p => p.LastSeenAt)
                .Take(Math.Clamp(limite, 1, 500))
                .Select(p => new
                {
                    p.Id,
                    tipo = p.Kind.ToString().ToLower(),
                    camara = p.CameraName,
                    primeraVez = p.FirstSeenAt,
                    ultimaVez = p.LastSeenAt,
                    apariciones = p.Occurrences,
                    matricula = p.PlateText,
                    clase = p.ObjectClass,
                    confianza = p.BestScore,
                    aprendible = p.Embedding != null || p.PlateText != null,
                })
                .ToListAsync(ct);

            return Ok(items);
        }
        catch (Exception ex)
        {
            return DatabaseUnavailable(ex);
        }
    }

    /// <summary>Fotograma anotado más reciente de una cámara, en JPEG.</summary>
    [HttpGet("instantanea/{camaraId:guid}")]
    public IActionResult Instantanea(Guid camaraId, [FromServices] IVZVision.Vision.Pipeline.FrameBroadcaster broadcaster)
    {
        var check = _tokens.Validate(Request, requireImages: true);
        if (!check.Ok) return Unauthorized(new { error = check.Error });

        var jpeg = broadcaster.GetLatest(camaraId);
        if (jpeg is null) return NotFound(new { error = "Esa cámara no tiene ningún fotograma disponible." });

        return File(jpeg, "image/jpeg");
    }

    /// <summary>
    /// Un cliente de la API necesita un error legible en JSON, no la página de error
    /// de la web: si SQL no responde se dice claramente y con el código adecuado.
    /// </summary>
    private IActionResult DatabaseUnavailable(Exception ex) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = "No se pudo consultar la base de datos.",
            detalle = ex.Message,
            sugerencia = "Revise los datos de conexión en Configuración → Base de datos.",
        });
}
