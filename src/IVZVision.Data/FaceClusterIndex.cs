using IVZVision.Core.Configuration;
using IVZVision.Core.Util;
using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IVZVision.Data;

/// <summary>
/// Agrupa los rostros que va viendo el sistema: cada cara nueva se compara con los
/// grupos ya formados y se suma al que se le parece, sin importar la cámara. Así
/// todas las fotos de la misma persona quedan bajo una única ficha, con nombre si
/// se lo han puesto o como «Rostro desconocido N» mientras tanto.
/// El índice vive en memoria para no consultar la base en cada fotograma.
/// </summary>
public sealed class FaceClusterIndex
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly IConfigStore _config;
    private readonly ILogger<FaceClusterIndex> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Entry> _clusters = new();
    private bool _loaded;

    public FaceClusterIndex(IDbContextFactory<VisionDbContext> dbFactory, IConfigStore config,
                            ILogger<FaceClusterIndex> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    private sealed class Entry
    {
        public int Id { get; init; }
        public required float[] Centroid { get; set; }
        public int SampleCount { get; set; }
    }

    /// <summary>Fuerza que la próxima asignación recargue los grupos desde la base.</summary>
    public void MarkDirty() => _loaded = false;

    public int ClusterCount => _clusters.Count;

    /// <summary>
    /// Devuelve el grupo al que pertenece este rostro, creando uno nuevo si no se
    /// parece a ninguno. Actualiza el centroide del grupo con la cara vista, de modo
    /// que el reconocimiento del grupo mejora con cada aparición.
    /// </summary>
    public async Task<int?> AssignAsync(float[] embedding, CancellationToken ct = default)
    {
        if (embedding.Length == 0) return null;

        var probe = VectorMath.L2Normalize(embedding);
        var rec = _config.Current.Recognition;
        var umbral = Math.Clamp(rec.FaceClusterSimilarity, 0.05f, 0.99f);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);

            // ¿A qué grupo se parece más?
            Entry? mejor = null;
            var mejorParecido = float.MinValue;

            foreach (var c in _clusters)
            {
                if (c.Centroid.Length != probe.Length) continue;

                float dot = 0;
                for (var i = 0; i < probe.Length; i++) dot += probe[i] * c.Centroid[i];

                if (dot > mejorParecido) { mejorParecido = dot; mejor = c; }
            }

            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            if (mejor is not null && mejorParecido >= umbral)
            {
                // Es la misma persona: el centroide se mueve un poco hacia esta cara.
                var n = Math.Max(1, mejor.SampleCount);
                var nuevo = new float[probe.Length];
                for (var i = 0; i < probe.Length; i++)
                    nuevo[i] = (mejor.Centroid[i] * n + probe[i]) / (n + 1);

                mejor.Centroid = VectorMath.L2Normalize(nuevo);
                mejor.SampleCount = n + 1;

                var fila = await db.FaceClusters.FirstOrDefaultAsync(f => f.Id == mejor.Id, ct).ConfigureAwait(false);
                if (fila is not null)
                {
                    fila.Centroid = VectorMath.ToBytes(mejor.Centroid);
                    fila.SampleCount = mejor.SampleCount;
                    fila.LastSeenAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                return mejor.Id;
            }

            // Cara nueva: se abre un grupo con el siguiente número correlativo.
            var siguiente = await db.FaceClusters.AnyAsync(ct).ConfigureAwait(false)
                ? await db.FaceClusters.MaxAsync(f => f.Numero, ct).ConfigureAwait(false) + 1
                : 1;

            var creado = new FaceCluster
            {
                Numero = siguiente,
                Centroid = VectorMath.ToBytes(probe),
                Dimensions = probe.Length,
                SampleCount = 1,
            };

            db.FaceClusters.Add(creado);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            _clusters.Add(new Entry { Id = creado.Id, Centroid = probe, SampleCount = 1 });
            _logger.LogInformation("Nuevo grupo de rostros: «Rostro desconocido {Numero}»", siguiente);

            return creado.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo agrupar el rostro");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var filas = await db.FaceClusters.AsNoTracking()
            .Select(f => new { f.Id, f.Centroid, f.SampleCount })
            .ToListAsync(ct).ConfigureAwait(false);

        _clusters.Clear();
        foreach (var f in filas)
        {
            var vector = VectorMath.FromBytes(f.Centroid);
            if (vector.Length == 0) continue;
            _clusters.Add(new Entry { Id = f.Id, Centroid = VectorMath.L2Normalize(vector), SampleCount = f.SampleCount });
        }

        _loaded = true;
        _logger.LogInformation("Índice de grupos de rostros cargado: {Count} grupo(s)", _clusters.Count);
    }
}
