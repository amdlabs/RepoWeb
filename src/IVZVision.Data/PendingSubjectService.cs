using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Core.Util;
using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IVZVision.Data;

public sealed record AssignResult(bool Success, string Message, int? EntityId = null);

/// <summary>
/// Gestiona la lista de sujetos que el sistema no supo identificar.
///
/// La idea es que el sistema aprenda con el uso: cada rostro, matrícula u objeto
/// desconocido queda en una ficha; cuando alguien le pone nombre, el vector de
/// características que ya se había calculado en el momento de la detección se
/// convierte en plantilla de reconocimiento. No hay que reprocesar la imagen ni
/// reentrenar nada: a partir de la siguiente recarga del índice, se reconoce.
/// </summary>
public sealed class PendingSubjectService
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly IConfigStore _config;
    private readonly KnownSubjectsIndex _index;
    private readonly ILogger<PendingSubjectService> _logger;
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    public PendingSubjectService(IDbContextFactory<VisionDbContext> dbFactory, IConfigStore config,
                                 KnownSubjectsIndex index, ILogger<PendingSubjectService> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _index = index;
        _logger = logger;
    }

    /// <summary>
    /// Registra un sujeto no identificado. Si ya hay una ficha del mismo sujeto se
    /// actualiza en vez de crear otra, para que la lista siga siendo revisable:
    /// los rostros se agrupan por parecido, las matrículas por texto y los objetos
    /// por clase (o por parecido si hay extractor de características).
    /// </summary>
    public async Task CaptureAsync(Observation observation, CancellationToken ct = default)
    {
        var recognition = _config.Current.Recognition;
        if (!recognition.QueueUnknownForLearning) return;
        if (observation.Match.IsKnown) return;

        var kind = ToKind(observation.Kind);
        if (kind is null) return;

        // Serializado: dos fotogramas seguidos del mismo desconocido no deben crear dos fichas.
        await _captureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var existing = await FindSimilarAsync(db, observation, kind.Value, recognition, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                existing.Occurrences++;
                existing.LastSeenAt = observation.Timestamp.UtcDateTime;
                existing.CameraId = observation.CameraId;
                existing.CameraName = Truncate(observation.CameraName, 150);

                // Se conserva siempre la mejor muestra: la de mayor confianza.
                if (observation.DetectionScore > existing.BestScore)
                {
                    existing.BestScore = observation.DetectionScore;
                    if (observation.CropPath is not null) existing.CropPath = Truncate(observation.CropPath, 400);
                    if (observation.Embedding is { Length: > 0 })
                    {
                        existing.Embedding = VectorMath.ToBytes(observation.Embedding);
                        existing.Dimensions = observation.Embedding.Length;
                    }
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            db.PendingSubjects.Add(new PendingSubject
            {
                Kind = kind.Value,
                Status = PendingStatus.Pending,
                CameraId = observation.CameraId,
                CameraName = Truncate(observation.CameraName, 150),
                FirstSeenAt = observation.Timestamp.UtcDateTime,
                LastSeenAt = observation.Timestamp.UtcDateTime,
                Occurrences = 1,
                Embedding = observation.Embedding is { Length: > 0 } ? VectorMath.ToBytes(observation.Embedding) : null,
                Dimensions = observation.Embedding?.Length ?? 0,
                PlateText = observation.PlateText is null ? null : Truncate(observation.PlateText, 20),
                ObjectClass = observation.ObjectClass is null ? null : Truncate(observation.ObjectClass, 80),
                BestScore = observation.DetectionScore,
                CropPath = observation.CropPath is null ? null : Truncate(observation.CropPath, 400),
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await TrimAsync(db, recognition.MaxPendingSubjects, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo registrar el sujeto desconocido en la lista de pendientes");
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private static async Task<PendingSubject?> FindSimilarAsync(
        VisionDbContext db, Observation observation, RecognitionKind kind,
        RecognitionConfig recognition, CancellationToken ct)
    {
        var pending = db.PendingSubjects.Where(p => p.Status == PendingStatus.Pending && p.Kind == kind);

        switch (kind)
        {
            case RecognitionKind.Plate:
                return await pending.FirstOrDefaultAsync(p => p.PlateText == observation.PlateText, ct)
                                    .ConfigureAwait(false);

            case RecognitionKind.Face:
            {
                if (observation.Embedding is not { Length: > 0 }) return null;

                var probe = VectorMath.L2Normalize(observation.Embedding);
                var candidates = await pending.ToListAsync(ct).ConfigureAwait(false);

                PendingSubject? best = null;
                var bestScore = recognition.FaceClusterThreshold;

                foreach (var candidate in candidates)
                {
                    var vector = VectorMath.FromBytes(candidate.Embedding);
                    if (vector.Length != probe.Length) continue;

                    var score = VectorMath.CosineSimilarity(probe, VectorMath.L2Normalize(vector));
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }

                return best;
            }

            case RecognitionKind.Object:
            {
                // Sin extractor de características sólo se puede agrupar por clase.
                if (observation.Embedding is not { Length: > 0 })
                    return await pending.FirstOrDefaultAsync(p => p.ObjectClass == observation.ObjectClass, ct)
                                        .ConfigureAwait(false);

                var probe = VectorMath.L2Normalize(observation.Embedding);
                var candidates = await pending.Where(p => p.ObjectClass == observation.ObjectClass)
                                              .ToListAsync(ct).ConfigureAwait(false);

                PendingSubject? best = null;
                var bestScore = recognition.ObjectMatchThreshold * 0.85f;

                foreach (var candidate in candidates)
                {
                    var vector = VectorMath.FromBytes(candidate.Embedding);
                    if (vector.Length != probe.Length) continue;

                    var score = VectorMath.CosineSimilarity(probe, VectorMath.L2Normalize(vector));
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }

                return best;
            }

            default:
                return null;
        }
    }

    private async Task TrimAsync(VisionDbContext db, int max, CancellationToken ct)
    {
        if (max <= 0) return;

        var count = await db.PendingSubjects.CountAsync(p => p.Status == PendingStatus.Pending, ct)
                            .ConfigureAwait(false);
        if (count <= max) return;

        // Se descartan las fichas más antiguas y con menos apariciones.
        var excess = await db.PendingSubjects
            .Where(p => p.Status == PendingStatus.Pending)
            .OrderBy(p => p.Occurrences).ThenBy(p => p.LastSeenAt)
            .Take(count - max)
            .ToListAsync(ct).ConfigureAwait(false);

        db.PendingSubjects.RemoveRange(excess);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Asigna un rostro pendiente a una persona nueva o existente.</summary>
    public async Task<AssignResult> AssignFaceAsync(long pendingId, int? personId, string? newPersonName,
                                                    bool authorized, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var pending = await db.PendingSubjects.FirstOrDefaultAsync(p => p.Id == pendingId, ct).ConfigureAwait(false);
        if (pending is null) return new AssignResult(false, "La ficha ya no existe.");
        if (pending.Kind != RecognitionKind.Face) return new AssignResult(false, "La ficha no es un rostro.");
        if (pending.Embedding is null || pending.Embedding.Length == 0)
            return new AssignResult(false, "La ficha no tiene vector de características; no se puede aprender de ella.");

        Person person;
        if (personId is int id and > 0)
        {
            var found = await db.Persons.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
            if (found is null) return new AssignResult(false, "La persona indicada no existe.");
            person = found;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(newPersonName))
                return new AssignResult(false, "Indique un nombre o elija una persona existente.");

            person = new Person { FullName = newPersonName.Trim(), IsAuthorized = authorized };
            db.Persons.Add(person);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // El vector calculado al detectar pasa a ser plantilla: eso es el aprendizaje.
        db.FaceTemplates.Add(new FaceTemplate
        {
            PersonId = person.Id,
            Embedding = pending.Embedding,
            Dimensions = pending.Dimensions,
            ModelId = pending.ModelId,
            ImagePath = pending.CropPath,
            Quality = pending.BestScore,
        });

        pending.Status = PendingStatus.Assigned;
        pending.AssignedPersonId = person.Id;
        pending.ResolvedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RefreshIndexAsync(ct).ConfigureAwait(false);

        return new AssignResult(true, $"Rostro asignado a «{person.FullName}». Ya se reconoce.", person.Id);
    }

    /// <summary>Asigna una matrícula pendiente a un vehículo nuevo o existente.</summary>
    public async Task<AssignResult> AssignPlateAsync(long pendingId, int? vehicleId, string? description,
                                                     int? ownerPersonId, bool authorized, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var pending = await db.PendingSubjects.FirstOrDefaultAsync(p => p.Id == pendingId, ct).ConfigureAwait(false);
        if (pending is null) return new AssignResult(false, "La ficha ya no existe.");
        if (pending.Kind != RecognitionKind.Plate) return new AssignResult(false, "La ficha no es una matrícula.");

        var plate = PlateText.Normalize(pending.PlateText);
        if (plate.Length == 0) return new AssignResult(false, "La ficha no tiene una matrícula legible.");

        Vehicle vehicle;
        if (vehicleId is int id and > 0)
        {
            var found = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct).ConfigureAwait(false);
            if (found is null) return new AssignResult(false, "El vehículo indicado no existe.");
            vehicle = found;
        }
        else
        {
            var duplicate = await db.Vehicles.FirstOrDefaultAsync(v => v.Plate == plate, ct).ConfigureAwait(false);
            if (duplicate is not null)
            {
                vehicle = duplicate;
            }
            else
            {
                vehicle = new Vehicle
                {
                    Plate = plate,
                    PlateRaw = pending.PlateText,
                    Make = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    OwnerPersonId = ownerPersonId is > 0 ? ownerPersonId : null,
                    IsAuthorized = authorized,
                };
                db.Vehicles.Add(vehicle);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        pending.Status = PendingStatus.Assigned;
        pending.AssignedVehicleId = vehicle.Id;
        pending.ResolvedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RefreshIndexAsync(ct).ConfigureAwait(false);

        return new AssignResult(true, $"Matrícula {plate} registrada. Ya se reconoce.", vehicle.Id);
    }

    /// <summary>Asigna un objeto pendiente a un objeto nombrado, nuevo o existente.</summary>
    public async Task<AssignResult> AssignObjectAsync(long pendingId, int? knownObjectId, string? newName,
                                                      bool authorized, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var pending = await db.PendingSubjects.FirstOrDefaultAsync(p => p.Id == pendingId, ct).ConfigureAwait(false);
        if (pending is null) return new AssignResult(false, "La ficha ya no existe.");
        if (pending.Kind != RecognitionKind.Object) return new AssignResult(false, "La ficha no es un objeto.");

        KnownObject known;
        if (knownObjectId is int id and > 0)
        {
            var found = await db.KnownObjects.FirstOrDefaultAsync(o => o.Id == id, ct).ConfigureAwait(false);
            if (found is null) return new AssignResult(false, "El objeto indicado no existe.");
            known = found;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(newName))
                return new AssignResult(false, "Indique un nombre o elija un objeto existente.");

            known = new KnownObject
            {
                Name = newName.Trim(),
                ObjectClass = pending.ObjectClass,
                IsAuthorized = authorized,
            };
            db.KnownObjects.Add(known);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var learned = false;
        if (pending.Embedding is { Length: > 0 })
        {
            db.ObjectTemplates.Add(new ObjectTemplate
            {
                KnownObjectId = known.Id,
                Embedding = pending.Embedding,
                Dimensions = pending.Dimensions,
                ModelId = pending.ModelId,
                ImagePath = pending.CropPath,
            });
            learned = true;
        }

        pending.Status = PendingStatus.Assigned;
        pending.AssignedObjectId = known.Id;
        pending.ResolvedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RefreshIndexAsync(ct).ConfigureAwait(false);

        var message = learned
            ? $"Objeto «{known.Name}» registrado. Ya se reconoce por su apariencia."
            : $"Objeto «{known.Name}» registrado en el catálogo. Para reconocerlo por su apariencia " +
              "configure un extractor de características de objetos.";

        return new AssignResult(true, message, known.Id);
    }

    public async Task<bool> SetStatusAsync(long pendingId, PendingStatus status, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var pending = await db.PendingSubjects.FirstOrDefaultAsync(p => p.Id == pendingId, ct).ConfigureAwait(false);
        if (pending is null) return false;

        pending.Status = status;
        pending.ResolvedAt = status == PendingStatus.Pending ? null : DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteAsync(long pendingId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var pending = await db.PendingSubjects.FirstOrDefaultAsync(p => p.Id == pendingId, ct).ConfigureAwait(false);
        if (pending is null) return false;

        db.PendingSubjects.Remove(pending);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.PendingSubjects.CountAsync(p => p.Status == PendingStatus.Pending, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private async Task RefreshIndexAsync(CancellationToken ct)
    {
        _index.MarkDirty();
        await _index.RefreshAsync(ct).ConfigureAwait(false);
    }

    private static RecognitionKind? ToKind(ObservationKind kind) => kind switch
    {
        ObservationKind.Face => RecognitionKind.Face,
        ObservationKind.Plate => RecognitionKind.Plate,
        ObservationKind.Object => RecognitionKind.Object,
        _ => null,   // códigos, texto y alertas no se aprenden
    };

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max];
    }
}
