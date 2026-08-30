using System.Collections.Concurrent;
using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IVZVision.Data;

/// <summary>Aviso que produce la memoria de escena: un objeto que faltó o que volvió.</summary>
public sealed record SceneChange(string ObjectClass, string Mensaje, double XPercent, double YPercent,
                                 double WidthPercent, double HeightPercent, string? CropBase64);

/// <summary>
/// Memoria de lo que cada cámara tiene delante. Un objeto quieto (unos libros, una
/// caja) se aprende la primera vez y deja de anunciarse mientras siga en su sitio;
/// cuando desaparece de la escena se avisa diciendo cuándo se le vio por última vez
/// y junto a qué otras cosas estaba, y si vuelve, se anota el regreso.
/// Las personas, vehículos y animales quedan fuera: se mueven por naturaleza.
/// </summary>
public sealed class SceneMemoryIndex
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly IConfigStore _config;
    private readonly ILogger<SceneMemoryIndex> _logger;

    /// <summary>Distancia máxima entre centros (en % del fotograma) para ser «el mismo sitio».</summary>
    private const double SameSpotPercent = 7.0;

    /// <summary>Análisis seguidos sin ver el objeto antes de darlo por ausente.</summary>
    private const int MissesBeforeGone = 10;

    private sealed class Tracked
    {
        public int Id;                       // 0 mientras no se haya guardado
        public required string ObjectClass;
        public double X, Y, W, H;            // en % del fotograma
        public DateTime FirstSeenAt = DateTime.UtcNow;
        public DateTime LastSeenAt = DateTime.UtcNow;
        public DateTime LastSavedAt = DateTime.MinValue;
        public int TimesSeen = 1;
        public int Misses;
        public bool Present = true;
        public string Neighbors = "";
        public string? CropBase64;
    }

    private sealed class CameraMemory
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<Tracked> Objetos = new();
        public bool Loaded;
    }

    private readonly ConcurrentDictionary<Guid, CameraMemory> _porCamara = new();

    // Los actores se mueven solos: no forman parte del atrezo de la escena.
    private static readonly HashSet<string> Actores = new(StringComparer.OrdinalIgnoreCase)
    {
        "person", "persona", "car", "coche", "auto", "truck", "camión", "camion", "bus", "autobús", "autobus",
        "motorcycle", "motocicleta", "moto", "bicycle", "bicicleta", "dog", "perro", "cat", "gato",
        "bird", "pájaro", "pajaro", "horse", "caballo",
    };

    public SceneMemoryIndex(IDbContextFactory<VisionDbContext> dbFactory, IConfigStore config,
                            ILogger<SceneMemoryIndex> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Cruza las detecciones de un análisis con lo que la cámara ya sabe que tiene delante.
    /// Quita de la lista los objetos quietos ya conocidos (no hace falta anunciarlos otra
    /// vez) y devuelve los cambios de escena: lo que faltó y lo que volvió.
    /// </summary>
    public async Task<IReadOnlyList<SceneChange>> ObserveAsync(Guid cameraId, List<Observation> observations,
                                                               int frameWidth, int frameHeight,
                                                               bool objectDetectionRan, CancellationToken ct = default)
    {
        if (!_config.Current.Recognition.SceneMemoryEnabled) return Array.Empty<SceneChange>();

        var memoria = _porCamara.GetOrAdd(cameraId, _ => new CameraMemory());
        var cambios = new List<SceneChange>();

        await memoria.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!memoria.Loaded) await LoadAsync(cameraId, memoria, ct).ConfigureAwait(false);

            var ahora = DateTime.UtcNow;

            // Clases presentes en este análisis, para anotar «junto a qué» está cada cosa.
            var escena = observations
                .Where(o => o.Kind == ObservationKind.Object && o.ObjectClass is not null)
                .Select(o => o.ObjectClass!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var emparejados = new HashSet<Tracked>();

            // 1) Cada objeto quieto detectado se busca en la memoria de la cámara.
            for (var i = observations.Count - 1; i >= 0; i--)
            {
                var obs = observations[i];
                if (obs.Kind != ObservationKind.Object || obs.ObjectClass is null) continue;
                if (Actores.Contains(obs.ObjectClass)) continue;
                if (frameWidth <= 0 || frameHeight <= 0) continue;

                var cx = (obs.Box.X + obs.Box.Width / 2) * 100.0 / frameWidth;
                var cy = (obs.Box.Y + obs.Box.Height / 2) * 100.0 / frameHeight;

                var conocido = memoria.Objetos.FirstOrDefault(t =>
                    !emparejados.Contains(t)
                    && string.Equals(t.ObjectClass, obs.ObjectClass, StringComparison.OrdinalIgnoreCase)
                    && Distancia(t, cx, cy) <= SameSpotPercent);

                if (conocido is null)
                {
                    // Objeto nuevo en la escena: se anuncia normalmente y se aprende su sitio.
                    var nuevo = new Tracked
                    {
                        ObjectClass = obs.ObjectClass,
                        X = cx, Y = cy,
                        W = obs.Box.Width * 100.0 / frameWidth,
                        H = obs.Box.Height * 100.0 / frameHeight,
                        Neighbors = Vecinos(escena, obs.ObjectClass),
                        CropBase64 = obs.CropJpegBase64,
                    };
                    memoria.Objetos.Add(nuevo);
                    emparejados.Add(nuevo);
                    await SaveAsync(cameraId, nuevo, ct).ConfigureAwait(false);
                    continue;
                }

                emparejados.Add(conocido);

                if (!conocido.Present)
                {
                    // Estaba dado por ausente y ha vuelto a su sitio.
                    conocido.Present = true;
                    conocido.Misses = 0;
                    cambios.Add(new SceneChange(conocido.ObjectClass,
                        $"«{conocido.ObjectClass}» volvió a su sitio",
                        conocido.X, conocido.Y, conocido.W, conocido.H, conocido.CropBase64));
                }

                // Ya se sabía que estaba ahí: se refresca su memoria y no se vuelve a anunciar.
                conocido.LastSeenAt = ahora;
                conocido.TimesSeen++;
                conocido.Misses = 0;
                conocido.X = cx;
                conocido.Y = cy;
                conocido.Neighbors = Vecinos(escena, conocido.ObjectClass);
                if (obs.CropJpegBase64 is not null) conocido.CropBase64 = obs.CropJpegBase64;

                if (ahora - conocido.LastSavedAt > TimeSpan.FromMinutes(5))
                    await SaveAsync(cameraId, conocido, ct).ConfigureAwait(false);

                observations.RemoveAt(i);
            }

            // 2) Lo que la memoria espera ver y este análisis no trajo. Sólo cuenta si el
            //    detector de objetos corrió: sin detector no hay ausencias, hay ceguera.
            if (objectDetectionRan)
            {
                var margen = TimeSpan.FromSeconds(Math.Max(10, _config.Current.Recognition.SceneObjectMissingSeconds));

                foreach (var t in memoria.Objetos.Where(t => t.Present && !emparejados.Contains(t)))
                {
                    t.Misses++;
                    if (t.Misses < MissesBeforeGone || ahora - t.LastSeenAt < margen) continue;

                    t.Present = false;
                    await SaveAsync(cameraId, t, ct).ConfigureAwait(false);

                    var junto = string.IsNullOrEmpty(t.Neighbors) ? "" : $"; estaba junto a: {t.Neighbors}";
                    cambios.Add(new SceneChange(t.ObjectClass,
                        $"«{t.ObjectClass}» ya no está donde estaba (visto por última vez " +
                        $"{t.LastSeenAt.ToLocalTime():dd/MM HH:mm}{junto})",
                        t.X, t.Y, t.W, t.H, t.CropBase64));
                }
            }

            return cambios;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "La memoria de escena falló en la cámara {Camera}", cameraId);
            return cambios;
        }
        finally
        {
            memoria.Gate.Release();
        }
    }

    private static double Distancia(Tracked t, double cx, double cy)
        => Math.Sqrt((t.X - cx) * (t.X - cx) + (t.Y - cy) * (t.Y - cy));

    private static string Vecinos(IReadOnlyList<string> escena, string propio)
    {
        var otros = escena.Where(c => !string.Equals(c, propio, StringComparison.OrdinalIgnoreCase)).Take(6);
        return string.Join(", ", otros);
    }

    private async Task LoadAsync(Guid cameraId, CameraMemory memoria, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var filas = await db.SceneObjects.AsNoTracking()
            .Where(o => o.CameraId == cameraId)
            .ToListAsync(ct).ConfigureAwait(false);

        memoria.Objetos.Clear();
        memoria.Objetos.AddRange(filas.Select(f => new Tracked
        {
            Id = f.Id,
            ObjectClass = f.ObjectClass,
            X = f.XPercent, Y = f.YPercent, W = f.WidthPercent, H = f.HeightPercent,
            FirstSeenAt = f.FirstSeenAt,
            LastSeenAt = f.LastSeenAt,
            LastSavedAt = DateTime.UtcNow,
            TimesSeen = f.TimesSeen,
            Present = f.IsPresent,
            Neighbors = f.LastNeighbors ?? "",
            CropBase64 = f.CropBase64,
        }));

        memoria.Loaded = true;
        if (memoria.Objetos.Count > 0)
            _logger.LogInformation("Memoria de escena de la cámara {Camera}: {Count} objeto(s) recordados",
                                   cameraId, memoria.Objetos.Count);
    }

    private async Task SaveAsync(Guid cameraId, Tracked t, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        SceneObject? fila = null;
        if (t.Id > 0)
            fila = await db.SceneObjects.FirstOrDefaultAsync(o => o.Id == t.Id, ct).ConfigureAwait(false);

        if (fila is null)
        {
            fila = new SceneObject { CameraId = cameraId, ObjectClass = t.ObjectClass, FirstSeenAt = t.FirstSeenAt };
            db.SceneObjects.Add(fila);
        }

        fila.XPercent = t.X;
        fila.YPercent = t.Y;
        fila.WidthPercent = t.W;
        fila.HeightPercent = t.H;
        fila.LastSeenAt = t.LastSeenAt;
        fila.TimesSeen = t.TimesSeen;
        fila.IsPresent = t.Present;
        fila.LastNeighbors = string.IsNullOrEmpty(t.Neighbors) ? null : Truncate(t.Neighbors, 400);
        fila.CropBase64 = t.CropBase64;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        t.Id = fila.Id;
        t.LastSavedAt = DateTime.UtcNow;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
