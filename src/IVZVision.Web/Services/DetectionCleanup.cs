using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Services;

/// <summary>
/// Borrado de registros capturados desde las pantallas de Detecciones. Elimina los
/// eventos y sus imágenes en disco; nunca toca el padrón (personas, vehículos ni
/// objetos etiquetados).
/// </summary>
public sealed class DetectionCleanup
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly IConfigStore _config;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DetectionCleanup> _logger;

    public DetectionCleanup(IDbContextFactory<VisionDbContext> dbFactory, IConfigStore config,
                            IWebHostEnvironment environment, ILogger<DetectionCleanup> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Borra los eventos que cumplan el filtro indicado.
    /// </summary>
    /// <param name="kinds">Tipos de evento a borrar.</param>
    /// <param name="objectClasses">Si se indica, limita a esas clases de objeto.</param>
    /// <param name="camera">Cámara concreta, o null para todas.</param>
    /// <param name="days">Antigüedad mínima en días; 0 o null borra todo lo que cumpla el resto del filtro.</param>
    /// <param name="onlyUnknown">Sólo los no identificados (conserva lo ya reconocido).</param>
    public async Task<(int Eventos, int Imagenes)> DeleteAsync(
        IReadOnlyCollection<RecognitionKind> kinds,
        IReadOnlyCollection<string>? objectClasses = null,
        string? camera = null,
        int? days = null,
        bool onlyUnknown = false,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.RecognitionEvents.Where(e => kinds.Contains(e.Kind));

        // El filtro de clases sólo restringe a los eventos de objeto: los rostros y
        // matrículas del mismo borrado no tienen clase y deben seguir incluidos.
        if (objectClasses is { Count: > 0 })
            query = query.Where(e => e.Kind != RecognitionKind.Object
                                     || (e.ObjectClass != null && objectClasses.Contains(e.ObjectClass)));

        if (!string.IsNullOrWhiteSpace(camera))
            query = query.Where(e => e.CameraName == camera);

        if (days is > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days.Value);
            query = query.Where(e => e.OccurredAt < cutoff);
        }

        if (onlyUnknown)
            query = query.Where(e => !e.IsKnown);

        // Las imágenes en disco se borran antes que las filas.
        var paths = await query.Where(e => e.CropPath != null).Select(e => e.CropPath!).ToListAsync(ct);
        var root = _config.Current.Storage.Resolve(_environment.ContentRootPath);

        var imagenes = 0;
        foreach (var path in paths)
        {
            try
            {
                var full = Path.Combine(root, path);
                if (File.Exists(full)) { File.Delete(full); imagenes++; }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo borrar el recorte {Path}", path);
            }
        }

        var eventos = await query.ExecuteDeleteAsync(ct);
        _logger.LogInformation("Limpieza de detecciones: {Eventos} evento(s) y {Imagenes} imagen(es)", eventos, imagenes);

        return (eventos, imagenes);
    }
}
