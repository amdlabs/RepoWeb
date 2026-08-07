using System.Collections.Concurrent;
using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IVZVision.Data;

/// <summary>
/// Persiste los reconocimientos aplicando un tiempo de guarda por sujeto y cámara,
/// de forma que una persona parada delante del objetivo no genere cientos de filas.
/// </summary>
public sealed class EventRecorder
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly IConfigStore _config;
    private readonly ILogger<EventRecorder> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();

    public EventRecorder(IDbContextFactory<VisionDbContext> dbFactory, IConfigStore config, ILogger<EventRecorder> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>true si el sujeto está dentro del tiempo de guarda y no debe registrarse otra vez.</summary>
    public bool IsThrottled(Observation obs)
    {
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _config.Current.Recognition.EventCooldownSeconds));
        if (cooldown <= TimeSpan.Zero) return false;

        var key = BuildKey(obs);
        var now = DateTimeOffset.UtcNow;

        // El delegado de actualización sólo refresca la marca cuando el tiempo ya venció.
        var throttled = false;
        _lastSeen.AddOrUpdate(key, now, (_, previous) =>
        {
            if (now - previous < cooldown)
            {
                throttled = true;
                return previous;
            }
            return now;
        });

        return throttled;
    }

    public async Task<long?> RecordAsync(Observation obs, RecognitionSource source = RecognitionSource.Local,
                                         CancellationToken ct = default)
    {
        var recognition = _config.Current.Recognition;
        if (!obs.Match.IsKnown && !recognition.RegisterUnknown)
            return null;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var entity = new RecognitionEvent
            {
                CameraId = obs.CameraId,
                CameraName = Truncate(obs.CameraName, 150),
                Kind = (RecognitionKind)(int)obs.Kind,
                Source = source,
                OccurredAt = obs.Timestamp.UtcDateTime,
                DetectionScore = obs.DetectionScore,
                MatchScore = obs.Match.Score,
                IsKnown = obs.Match.IsKnown,
                IsAuthorized = obs.Match.IsKnown && obs.Match.IsAuthorized,
                PersonId = obs.Match.PersonId,
                VehicleId = obs.Match.VehicleId,
                KnownObjectId = obs.Match.ObjectId,
                Label = Truncate(obs.DisplayLabel, 200),
                PlateText = obs.PlateText is null ? null : Truncate(obs.PlateText, 20),
                OcrConfidence = obs.OcrConfidence,
                ObjectClass = obs.ObjectClass is null ? null : Truncate(obs.ObjectClass, 80),
                CodeValue = obs.CodeValue is null ? null : Truncate(obs.CodeValue, 2000),
                CodeFormat = obs.CodeFormat is null ? null : Truncate(obs.CodeFormat, 40),
                TextValue = obs.TextValue is null ? null : Truncate(obs.TextValue, 2000),
                ActivityKind = (int)obs.Activity,
                Severity = (int)obs.Severity,
                Explanation = obs.Explanation is null ? null : Truncate(obs.Explanation, 500),
                BoxX = (int)Math.Round(obs.Box.X),
                BoxY = (int)Math.Round(obs.Box.Y),
                BoxWidth = (int)Math.Round(obs.Box.Width),
                BoxHeight = (int)Math.Round(obs.Box.Height),
                CropPath = obs.CropPath is null ? null : Truncate(obs.CropPath, 400),
            };

            db.RecognitionEvents.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            obs.EventId = entity.Id;
            return entity.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo registrar el evento de {Kind} en la cámara {Camera}", obs.Kind, obs.CameraName);
            return null;
        }
    }

    /// <summary>Borra eventos y recortes más antiguos que la retención configurada.</summary>
    public async Task<int> PurgeAsync(string snapshotsRoot, CancellationToken ct = default)
    {
        var days = _config.Current.Storage.RetentionDays;
        if (days <= 0) return 0;

        var cutoff = DateTime.UtcNow.AddDays(-days);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var stale = await db.RecognitionEvents
            .Where(e => e.OccurredAt < cutoff)
            .Select(e => new { e.Id, e.CropPath })
            .ToListAsync(ct).ConfigureAwait(false);

        if (stale.Count == 0) return 0;

        foreach (var item in stale.Where(s => !string.IsNullOrEmpty(s.CropPath)))
        {
            try
            {
                var full = Path.Combine(snapshotsRoot, item.CropPath!);
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo borrar el recorte {Path}", item.CropPath);
            }
        }

        var deleted = await db.RecognitionEvents
            .Where(e => e.OccurredAt < cutoff)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Purga de histórico: {Count} eventos anteriores a {Cutoff:u}", deleted, cutoff);
        return deleted;
    }

    private static string BuildKey(Observation obs)
    {
        var subject = obs.Kind switch
        {
            ObservationKind.Plate => obs.PlateText ?? "desconocida",
            ObservationKind.Code => obs.CodeValue ?? "codigo",
            ObservationKind.Text => obs.TextValue ?? "texto",
            // Las alertas se agrupan por objeto seguido: dos personas distintas
            // merodeando a la vez generan dos avisos, no uno.
            ObservationKind.Activity => $"{obs.Activity}|{obs.TrackId?.ToString() ?? "-"}",
            ObservationKind.Object => obs.Match.ObjectId?.ToString() ?? obs.ObjectClass ?? "objeto",
            _ => obs.Match.PersonId?.ToString() ?? "desconocido",
        };

        return $"{obs.CameraId}|{obs.Kind}|{subject}";
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max];
    }
}
