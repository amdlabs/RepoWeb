using IVZVision.Core.Detection;
using IVZVision.Core.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IVZVision.Data;

/// <summary>
/// Índice en memoria de los sujetos conocidos: embeddings faciales, matrículas y
/// objetos nombrados. Evita ir a SQL en cada fotograma: se recarga al arrancar, cada
/// <see cref="RefreshInterval"/> y cada vez que se da de alta o se edita un registro.
/// </summary>
public sealed class KnownSubjectsIndex
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly ILogger<KnownSubjectsIndex> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile FaceEntry[] _faces = Array.Empty<FaceEntry>();
    private volatile ObjectEntry[] _objects = Array.Empty<ObjectEntry>();
    private volatile Dictionary<string, PlateEntry> _plates = new(StringComparer.Ordinal);
    private long _dirty = 1;

    public KnownSubjectsIndex(IDbContextFactory<VisionDbContext> dbFactory, ILogger<KnownSubjectsIndex> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public DateTimeOffset? LastRefreshedAt { get; private set; }
    public int FaceTemplateCount => _faces.Length;
    public int PlateCount => _plates.Count;
    public int ObjectTemplateCount => _objects.Length;
    public string? LastError { get; private set; }

    /// <summary>Marca el índice como obsoleto para que se recargue en el siguiente ciclo.</summary>
    public void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    public bool IsDirty => Interlocked.Read(ref _dirty) == 1;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false))
            return; // ya hay una recarga en curso

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            _faces = await LoadFacesAsync(db, ct).ConfigureAwait(false);
            _plates = await LoadPlatesAsync(db, ct).ConfigureAwait(false);
            _objects = await LoadObjectsAsync(db, ct).ConfigureAwait(false);

            LastRefreshedAt = DateTimeOffset.Now;
            LastError = null;
            Interlocked.Exchange(ref _dirty, 0);

            _logger.LogInformation("Índice recargado: {Faces} rostros, {Plates} matrículas, {Objects} objetos",
                _faces.Length, _plates.Count, _objects.Length);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "No se pudo recargar el índice de sujetos conocidos");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<FaceEntry[]> LoadFacesAsync(VisionDbContext db, CancellationToken ct)
    {
        var templates = await db.FaceTemplates
            .AsNoTracking()
            .Where(t => t.Person != null && t.Person.IsActive)
            .Select(t => new
            {
                t.PersonId,
                t.Embedding,
                PersonName = t.Person!.FullName,
                t.Person.IsAuthorized,
                t.Person.Department,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var faces = new List<FaceEntry>(templates.Count);
        foreach (var t in templates)
        {
            var vector = VectorMath.FromBytes(t.Embedding);
            if (vector.Length == 0) continue;

            // Se guardan ya normalizados: el matching se reduce a un producto escalar.
            faces.Add(new FaceEntry(t.PersonId, t.PersonName, t.IsAuthorized, t.Department,
                                    VectorMath.L2Normalize(vector)));
        }

        return faces.ToArray();
    }

    private static async Task<Dictionary<string, PlateEntry>> LoadPlatesAsync(VisionDbContext db, CancellationToken ct)
    {
        var vehicles = await db.Vehicles
            .AsNoTracking()
            .Where(v => v.IsActive)
            .Select(v => new
            {
                v.Id,
                v.Plate,
                v.IsAuthorized,
                v.Make,
                v.Model,
                OwnerName = v.OwnerPerson != null ? v.OwnerPerson.FullName : null,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var plates = new Dictionary<string, PlateEntry>(vehicles.Count, StringComparer.Ordinal);
        foreach (var v in vehicles)
        {
            var key = PlateText.Normalize(v.Plate);
            if (key.Length == 0) continue;

            var descriptor = string.Join(' ', new[] { v.Make, v.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var label = v.OwnerName ?? (string.IsNullOrWhiteSpace(descriptor) ? "Vehículo registrado" : descriptor);
            plates[key] = new PlateEntry(v.Id, label, v.IsAuthorized, descriptor);
        }

        return plates;
    }

    private static async Task<ObjectEntry[]> LoadObjectsAsync(VisionDbContext db, CancellationToken ct)
    {
        var templates = await db.ObjectTemplates
            .AsNoTracking()
            .Where(t => t.KnownObject != null && t.KnownObject.IsActive)
            .Select(t => new
            {
                t.KnownObjectId,
                t.Embedding,
                Name = t.KnownObject!.Name,
                t.KnownObject.ObjectClass,
                t.KnownObject.IsAuthorized,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var objects = new List<ObjectEntry>(templates.Count);
        foreach (var t in templates)
        {
            var vector = VectorMath.FromBytes(t.Embedding);
            if (vector.Length == 0) continue;

            objects.Add(new ObjectEntry(t.KnownObjectId, t.Name, t.ObjectClass, t.IsAuthorized,
                                        VectorMath.L2Normalize(vector)));
        }

        return objects.ToArray();
    }

    /// <summary>Busca la persona más parecida al embedding dado.</summary>
    public IdentityMatch MatchFace(float[] embedding, float threshold)
    {
        var faces = _faces;
        if (faces.Length == 0 || embedding.Length == 0) return IdentityMatch.Unknown;

        var probe = VectorMath.L2Normalize(embedding);

        var bestScore = float.MinValue;
        FaceEntry? best = null;

        foreach (var entry in faces)
        {
            if (entry.Embedding.Length != probe.Length) continue;

            float dot = 0;
            for (var i = 0; i < probe.Length; i++)
                dot += probe[i] * entry.Embedding[i];

            if (dot > bestScore)
            {
                bestScore = dot;
                best = entry;
            }
        }

        if (best is null || bestScore < threshold)
            return new IdentityMatch { IsKnown = false, Label = "Desconocido", Score = Math.Max(0, bestScore) };

        return new IdentityMatch
        {
            IsKnown = true,
            PersonId = best.PersonId,
            Label = best.Name,
            Score = bestScore,
            IsAuthorized = best.IsAuthorized,
            Notes = best.Department,
        };
    }

    /// <summary>Busca la matrícula normalizada en el padrón de vehículos.</summary>
    public IdentityMatch MatchPlate(string normalizedPlate)
    {
        if (string.IsNullOrEmpty(normalizedPlate)) return IdentityMatch.Unknown;

        if (_plates.TryGetValue(normalizedPlate, out var entry))
        {
            return new IdentityMatch
            {
                IsKnown = true,
                VehicleId = entry.VehicleId,
                Label = entry.Label,
                Score = 1f,
                IsAuthorized = entry.IsAuthorized,
                Notes = entry.Descriptor,
            };
        }

        return new IdentityMatch { IsKnown = false, Label = "No registrado", Score = 0 };
    }

    /// <summary>
    /// Busca un objeto nombrado por su apariencia. Sólo funciona si hay un extractor
    /// de características de objetos configurado; si no, devuelve desconocido.
    /// </summary>
    public IdentityMatch MatchObject(float[]? embedding, string? objectClass, float threshold)
    {
        var objects = _objects;
        if (objects.Length == 0 || embedding is null || embedding.Length == 0)
            return IdentityMatch.Unknown;

        var probe = VectorMath.L2Normalize(embedding);

        var bestScore = float.MinValue;
        ObjectEntry? best = null;

        foreach (var entry in objects)
        {
            if (entry.Embedding.Length != probe.Length) continue;

            // Un objeto sólo puede coincidir con otro de su misma clase.
            if (!string.IsNullOrEmpty(objectClass) && !string.IsNullOrEmpty(entry.ObjectClass)
                && !string.Equals(objectClass, entry.ObjectClass, StringComparison.OrdinalIgnoreCase))
                continue;

            float dot = 0;
            for (var i = 0; i < probe.Length; i++)
                dot += probe[i] * entry.Embedding[i];

            if (dot > bestScore)
            {
                bestScore = dot;
                best = entry;
            }
        }

        if (best is null || bestScore < threshold)
            return new IdentityMatch { IsKnown = false, Label = objectClass ?? "objeto", Score = Math.Max(0, bestScore) };

        return new IdentityMatch
        {
            IsKnown = true,
            ObjectId = best.KnownObjectId,
            Label = best.Name,
            Score = bestScore,
            IsAuthorized = best.IsAuthorized,
            Notes = best.ObjectClass,
        };
    }

    private sealed record FaceEntry(int PersonId, string Name, bool IsAuthorized, string? Department, float[] Embedding);

    private sealed record PlateEntry(int VehicleId, string Label, bool IsAuthorized, string? Descriptor);

    private sealed record ObjectEntry(int KnownObjectId, string Name, string? ObjectClass, bool IsAuthorized, float[] Embedding);
}
