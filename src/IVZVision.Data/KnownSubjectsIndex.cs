using IVZVision.Core.Detection;
using IVZVision.Core.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IVZVision.Data;

/// <summary>
/// Índice en memoria de los sujetos conocidos (embeddings faciales y matrículas).
/// Evita ir a SQL en cada fotograma: se recarga al arrancar, cada
/// <see cref="RefreshInterval"/> y cada vez que se da de alta o se edita un registro.
/// </summary>
public sealed class KnownSubjectsIndex
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly ILogger<KnownSubjectsIndex> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile FaceEntry[] _faces = Array.Empty<FaceEntry>();
    private volatile Dictionary<string, PlateEntry> _plates = new(StringComparer.Ordinal);
    private volatile Dictionary<string, ObjectEntry> _objects = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, string> _plateCorrections = new(StringComparer.Ordinal);
    private long _dirty = 1;

    public KnownSubjectsIndex(IDbContextFactory<VisionDbContext> dbFactory, ILogger<KnownSubjectsIndex> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public DateTimeOffset? LastRefreshedAt { get; private set; }
    public int FaceTemplateCount => _faces.Length;
    public int PlateCount => _plates.Count;
    public int ObjectLabelCount => _objects.Count;
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

            var labels = await db.ObjectLabels
                .AsNoTracking()
                .Where(o => o.IsActive)
                .ToListAsync(ct).ConfigureAwait(false);

            var objects = new Dictionary<string, ObjectEntry>(labels.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var o in labels)
            {
                var key = o.ClassName.Trim();
                if (key.Length == 0) continue;
                objects[key] = new ObjectEntry(o.Id, o.DisplayName, o.IsAuthorized, o.Notes);
            }

            // Correcciones de matrícula enseñadas por el usuario: el OCR las aplica
            // en cuanto vuelve a leer el mismo texto erróneo.
            var corrections = await db.PlateCorrections
                .AsNoTracking()
                .ToDictionaryAsync(c => c.WrongText, c => c.CorrectText, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

            _faces = faces.ToArray();
            _plates = plates;
            _objects = objects;
            _plateCorrections = corrections;
            LastRefreshedAt = DateTimeOffset.Now;
            LastError = null;
            Interlocked.Exchange(ref _dirty, 0);

            _logger.LogInformation("Índice recargado: {Faces} plantillas faciales, {Plates} matrículas, {Objects} objetos etiquetados",
                _faces.Length, _plates.Count, _objects.Count);
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

    /// <summary>
    /// Aplica lo aprendido de las correcciones del usuario: si el OCR devuelve un
    /// texto que ya se corrigió antes, se sustituye por el correcto. Devuelve el
    /// texto tal cual si no hay nada aprendido para él.
    /// </summary>
    public string ApplyLearnedCorrection(string normalizedPlate)
    {
        if (string.IsNullOrEmpty(normalizedPlate)) return normalizedPlate;
        return _plateCorrections.TryGetValue(normalizedPlate, out var corrected) ? corrected : normalizedPlate;
    }

    public int PlateCorrectionCount => _plateCorrections.Count;

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
    /// Busca la clase de objeto entre las etiquetadas por el usuario. Si nadie la ha
    /// etiquetado todavía, la detección se lista como objeto desconocido.
    /// </summary>
    public IdentityMatch MatchObject(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return IdentityMatch.Unknown;

        if (_objects.TryGetValue(className.Trim(), out var entry))
        {
            return new IdentityMatch
            {
                IsKnown = true,
                Label = entry.DisplayName,
                Score = 1f,
                IsAuthorized = entry.IsAuthorized,
                Notes = entry.Notes,
            };
        }

        return new IdentityMatch { IsKnown = false, Label = "Sin etiquetar", Score = 0 };
    }

    private sealed record FaceEntry(int PersonId, string Name, bool IsAuthorized, string? Department, float[] Embedding);

    private sealed record PlateEntry(int VehicleId, string Label, bool IsAuthorized, string? Descriptor);

    private sealed record ObjectEntry(int LabelId, string DisplayName, bool IsAuthorized, string? Notes);
}
