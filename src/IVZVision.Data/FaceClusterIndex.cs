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

                // Cada cara nueva mueve un poco el centroide; si con ese movimiento el
                // grupo quedó pegado a otro, es que siempre fueron la misma persona:
                // se fusionan solos y la cara promedio se recalcula ponderada.
                var autofusion = Math.Clamp(rec.FaceClusterAutoMergeSimilarity, umbral, 0.99f);
                var gemelo = BuscarMasParecido(mejor, autofusion);
                if (gemelo is not null)
                {
                    _logger.LogInformation("Grupos {A} y {B} superan el {Umbral:P0} de parecido: se fusionan solos",
                                           mejor.Id, gemelo.Id, autofusion);
                    var superviviente = await MergeLockedAsync(new[] { mejor.Id, gemelo.Id }, ct).ConfigureAwait(false);
                    if (superviviente is not null) return superviviente;
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

    /// <summary>
    /// Une varios grupos en uno solo porque son la misma persona vista en distintos
    /// ángulos o cámaras. La cara promedio del grupo resultante se calcula ponderando
    /// cada grupo por las fotos que aporta, de modo que ninguno domine al resto: así
    /// el sistema aprende esa cara «de frente y de lado» en lugar de sólo una pose.
    /// Devuelve el grupo que sobrevive.
    /// </summary>
    public async Task<int?> MergeAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        if (ids.Count < 2) return ids.Count == 1 ? ids[0] : null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await MergeLockedAsync(ids, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>El grupo que más se parece a éste por encima del umbral, o null. Exige el cerrojo tomado.</summary>
    private Entry? BuscarMasParecido(Entry grupo, float umbral)
    {
        Entry? mejor = null;
        var mejorParecido = umbral;

        foreach (var c in _clusters)
        {
            if (c.Id == grupo.Id || c.Centroid.Length != grupo.Centroid.Length) continue;

            float dot = 0;
            for (var i = 0; i < grupo.Centroid.Length; i++) dot += grupo.Centroid[i] * c.Centroid[i];

            if (dot >= mejorParecido) { mejorParecido = dot; mejor = c; }
        }

        return mejor;
    }

    /// <summary>Núcleo de la fusión. Exige tener tomado <see cref="_gate"/>.</summary>
    private async Task<int?> MergeLockedAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var filas = await db.FaceClusters.Where(c => ids.Contains(c.Id))
                .ToListAsync(ct).ConfigureAwait(false);

            if (filas.Count < 2) return filas.FirstOrDefault()?.Id;

            // Sobrevive el que ya tiene nombre; si ninguno lo tiene, el más antiguo.
            var destino = filas.FirstOrDefault(f => f.PersonId is not null || !string.IsNullOrWhiteSpace(f.Label))
                          ?? filas.OrderBy(f => f.Numero).First();

            var dimensiones = filas.Select(f => VectorMath.FromBytes(f.Centroid))
                                   .Where(v => v.Length > 0)
                                   .Select(v => v.Length)
                                   .DefaultIfEmpty(0)
                                   .Max();

            if (dimensiones == 0) return destino.Id;

            var suma = new float[dimensiones];
            var total = 0;

            foreach (var f in filas)
            {
                var vector = VectorMath.FromBytes(f.Centroid);
                if (vector.Length != dimensiones) continue;

                var peso = Math.Max(1, f.SampleCount);
                for (var i = 0; i < dimensiones; i++) suma[i] += vector[i] * peso;
                total += peso;
            }

            if (total == 0) return destino.Id;

            for (var i = 0; i < dimensiones; i++) suma[i] /= total;
            var centroide = VectorMath.L2Normalize(suma);

            destino.Centroid = VectorMath.ToBytes(centroide);
            destino.Dimensions = dimensiones;
            destino.SampleCount = total;
            destino.FirstSeenAt = filas.Min(f => f.FirstSeenAt);
            destino.LastSeenAt = filas.Max(f => f.LastSeenAt);
            destino.Label ??= filas.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Label))?.Label;
            destino.PersonId ??= filas.FirstOrDefault(f => f.PersonId is not null)?.PersonId;

            var absorbidos = filas.Where(f => f.Id != destino.Id).Select(f => f.Id).ToList();

            // Las fotos de los grupos absorbidos pasan al que sobrevive.
            await db.RecognitionEvents
                .Where(e => e.FaceClusterId != null && absorbidos.Contains(e.FaceClusterId!.Value))
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.FaceClusterId, destino.Id), ct)
                .ConfigureAwait(false);

            db.FaceClusters.RemoveRange(filas.Where(f => f.Id != destino.Id));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            _loaded = false; // el índice en memoria se rehace con la nueva cara promedio
            _logger.LogInformation("Grupos de rostros unificados: {Absorbidos} → «{Destino}» ({Fotos} muestras)",
                                   string.Join(", ", absorbidos), destino.DisplayName, total);

            // Si el grupo superviviente ya tiene nombre, el histórico absorbido lo hereda.
            if (destino.PersonId is int dueno)
            {
                await db.RecognitionEvents
                    .Where(e => e.FaceClusterId == destino.Id && !e.IsKnown)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.Label, destino.DisplayName)
                                              .SetProperty(e => e.PersonId, (int?)dueno)
                                              .SetProperty(e => e.IsKnown, true), ct)
                    .ConfigureAwait(false);
            }

            return destino.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron unificar los grupos de rostros");
            return null;
        }
    }

    /// <summary>Un grupo dentro de la lista ordenada, con lo que se parece al anterior.</summary>
    public sealed record ClusterOrder(int Id, float SimilitudPrevia);

    /// <summary>
    /// Ordena los grupos poniendo juntos los que más se parecen entre sí, de manera
    /// que las caras candidatas a ser la misma persona caigan una al lado de la otra
    /// y unificarlas sea cuestión de mirar dos fichas contiguas.
    /// Se recorre en cadena: se arranca por el grupo con más fotos y cada paso elige,
    /// de los que quedan, el más parecido al último colocado.
    /// </summary>
    public async Task<IReadOnlyList<ClusterOrder>> OrderBySimilarityAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var filas = await db.FaceClusters.AsNoTracking()
            .Select(f => new { f.Id, f.Centroid, f.SampleCount })
            .ToListAsync(ct).ConfigureAwait(false);

        var vectores = filas
            .Select(f => new { f.Id, f.SampleCount, Vector = VectorMath.L2Normalize(VectorMath.FromBytes(f.Centroid)) })
            .Where(f => f.Vector.Length > 0)
            .ToList();

        if (vectores.Count == 0) return Array.Empty<ClusterOrder>();

        var pendientes = vectores.OrderByDescending(v => v.SampleCount).ToList();
        var orden = new List<ClusterOrder>(pendientes.Count);

        var actual = pendientes[0];
        pendientes.RemoveAt(0);
        orden.Add(new ClusterOrder(actual.Id, 0));

        while (pendientes.Count > 0)
        {
            var mejorIndice = 0;
            var mejorParecido = float.MinValue;

            for (var i = 0; i < pendientes.Count; i++)
            {
                var otro = pendientes[i];
                if (otro.Vector.Length != actual.Vector.Length) continue;

                float dot = 0;
                for (var j = 0; j < actual.Vector.Length; j++) dot += actual.Vector[j] * otro.Vector[j];

                if (dot > mejorParecido) { mejorParecido = dot; mejorIndice = i; }
            }

            actual = pendientes[mejorIndice];
            pendientes.RemoveAt(mejorIndice);
            orden.Add(new ClusterOrder(actual.Id, mejorParecido == float.MinValue ? 0 : mejorParecido));
        }

        return orden;
    }

    /// <summary>
    /// Rehace la cara promedio de un grupo a partir de las caras que le quedan.
    /// Se usa al sacar una foto que no pertenecía al grupo: sin esto, esa cara
    /// seguiría pesando en el promedio y el grupo seguiría atrayendo caras ajenas.
    /// </summary>
    public async Task<bool> RecomputeCentroidAsync(int clusterId, IReadOnlyList<float[]> embeddings,
                                                   CancellationToken ct = default)
    {
        if (embeddings.Count == 0) return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dimensiones = embeddings[0].Length;
            var suma = new float[dimensiones];
            var usadas = 0;

            foreach (var e in embeddings)
            {
                if (e.Length != dimensiones) continue;
                var v = VectorMath.L2Normalize(e);
                for (var i = 0; i < dimensiones; i++) suma[i] += v[i];
                usadas++;
            }

            if (usadas == 0) return false;

            for (var i = 0; i < dimensiones; i++) suma[i] /= usadas;
            var centroide = VectorMath.L2Normalize(suma);

            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var fila = await db.FaceClusters.FirstOrDefaultAsync(f => f.Id == clusterId, ct).ConfigureAwait(false);
            if (fila is null) return false;

            fila.Centroid = VectorMath.ToBytes(centroide);
            fila.Dimensions = dimensiones;
            fila.SampleCount = Math.Max(1, usadas);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            _loaded = false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo recalcular la cara promedio del grupo {Grupo}", clusterId);
            return false;
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
