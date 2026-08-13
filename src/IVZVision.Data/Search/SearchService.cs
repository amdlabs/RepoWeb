using IVZVision.Core.Configuration;
using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Data.Search;

public sealed record SearchHit(
    long Id,
    string Origen,              // "evento" o "pendiente"
    RecognitionKind Kind,
    DateTime OccurredAt,
    string CameraName,
    string Label,
    string? PlateText,
    string? ObjectClass,
    string? CodeValue,
    string? TextValue,
    bool IsKnown,
    bool IsAuthorized,
    float Score,
    string? Explanation,
    string? CropPath,
    int Occurrences);

public sealed record SearchResult(SearchQuery Query, IReadOnlyList<SearchHit> Hits, int Total, string Interpretation);

/// <summary>
/// Buscador sobre lo que el sistema ha visto: histórico de eventos y fichas
/// pendientes de nombrar. Acepta una frase en castellano o filtros ya estructurados.
/// </summary>
public sealed class SearchService
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly IConfigStore _config;

    public SearchService(IDbContextFactory<VisionDbContext> dbFactory, IConfigStore config)
    {
        _dbFactory = dbFactory;
        _config = config;
    }

    /// <summary>Interpreta la frase y ejecuta la búsqueda.</summary>
    public async Task<SearchResult> SearchAsync(string? prompt, int take = 50,
                                                bool includePending = true, CancellationToken ct = default)
    {
        var cameras = _config.Current.Cameras.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        var query = PromptParser.Parse(prompt, DateTimeOffset.Now, cameras);
        query.Take = Math.Clamp(take, 1, 500);

        return await SearchAsync(query, includePending, ct).ConfigureAwait(false);
    }

    public async Task<SearchResult> SearchAsync(SearchQuery query, bool includePending = true,
                                                CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var hits = new List<SearchHit>();

        var events = BuildEventQuery(db, query);
        var total = await events.CountAsync(ct).ConfigureAwait(false);

        hits.AddRange(await events
            .OrderByDescending(e => e.OccurredAt)
            .Take(query.Take)
            .Select(e => new SearchHit(
                e.Id, "evento", e.Kind, e.OccurredAt, e.CameraName, e.Label, e.PlateText,
                e.ObjectClass, e.CodeValue, e.TextValue, e.IsKnown, e.IsAuthorized,
                e.IsKnown ? e.MatchScore : e.DetectionScore, e.Explanation, e.CropPath, 1))
            .ToListAsync(ct).ConfigureAwait(false));

        // Las fichas pendientes son justamente "lo desconocido": interesan cuando se
        // busca eso, y estorban cuando se piden identificados.
        if (includePending && query.OnlyKnown != true && !query.OnlyAlerts)
        {
            var pending = BuildPendingQuery(db, query);

            hits.AddRange(await pending
                .OrderByDescending(p => p.LastSeenAt)
                .Take(query.Take)
                .Select(p => new SearchHit(
                    p.Id, "pendiente", p.Kind, p.LastSeenAt, p.CameraName,
                    p.SuggestedName ?? p.PlateText ?? p.ObjectClass ?? "Sin identificar",
                    p.PlateText, p.ObjectClass, null, null, false, false,
                    p.BestScore, null, p.CropPath, p.Occurrences))
                .ToListAsync(ct).ConfigureAwait(false));
        }

        var ordered = hits.OrderByDescending(h => h.OccurredAt).Take(query.Take).ToList();

        return new SearchResult(query, ordered, total, query.Describe());
    }

    private static IQueryable<RecognitionEvent> BuildEventQuery(VisionDbContext db, SearchQuery q)
    {
        var query = db.RecognitionEvents.AsNoTracking().AsQueryable();

        if (q.OnlyAlerts) query = query.Where(e => e.Kind == RecognitionKind.Activity);
        else if (q.Kind is RecognitionKind kind) query = query.Where(e => e.Kind == kind);

        if (q.OnlyKnown == true) query = query.Where(e => e.IsKnown);
        else if (q.OnlyKnown == false) query = query.Where(e => !e.IsKnown);

        if (q.OnlyUnauthorized) query = query.Where(e => e.IsKnown && !e.IsAuthorized);

        if (q.CameraId is Guid cameraId) query = query.Where(e => e.CameraId == cameraId);

        if (q.FromUtc is DateTime from) query = query.Where(e => e.OccurredAt >= from);
        if (q.ToUtc is DateTime to) query = query.Where(e => e.OccurredAt < to);

        if (q.ObjectClass == "@animal")
        {
            var animals = PromptParser.AnimalClassNames;
            query = query.Where(e => e.ObjectClass != null && animals.Contains(e.ObjectClass));
        }
        else if (!string.IsNullOrEmpty(q.ObjectClass))
        {
            query = query.Where(e => e.ObjectClass == q.ObjectClass);
        }

        if (!string.IsNullOrWhiteSpace(q.FreeText))
        {
            var term = q.FreeText.Trim();
            query = query.Where(e => e.Label.Contains(term)
                                  || (e.PlateText != null && e.PlateText.Contains(term))
                                  || (e.CodeValue != null && e.CodeValue.Contains(term))
                                  || (e.TextValue != null && e.TextValue.Contains(term))
                                  || (e.ObjectClass != null && e.ObjectClass.Contains(term)));
        }

        return query;
    }

    private static IQueryable<PendingSubject> BuildPendingQuery(VisionDbContext db, SearchQuery q)
    {
        var query = db.PendingSubjects.AsNoTracking()
            .Where(p => p.Status == PendingStatus.Pending);

        if (q.Kind is RecognitionKind kind) query = query.Where(p => p.Kind == kind);
        if (q.CameraId is Guid cameraId) query = query.Where(p => p.CameraId == cameraId);
        if (q.FromUtc is DateTime from) query = query.Where(p => p.LastSeenAt >= from);
        if (q.ToUtc is DateTime to) query = query.Where(p => p.LastSeenAt < to);

        if (q.ObjectClass == "@animal")
        {
            var animals = PromptParser.AnimalClassNames;
            query = query.Where(p => p.ObjectClass != null && animals.Contains(p.ObjectClass));
        }
        else if (!string.IsNullOrEmpty(q.ObjectClass))
        {
            query = query.Where(p => p.ObjectClass == q.ObjectClass);
        }

        if (!string.IsNullOrWhiteSpace(q.FreeText))
        {
            var term = q.FreeText.Trim();
            query = query.Where(p => (p.PlateText != null && p.PlateText.Contains(term))
                                  || (p.ObjectClass != null && p.ObjectClass.Contains(term))
                                  || (p.SuggestedName != null && p.SuggestedName.Contains(term)));
        }

        return query;
    }
}
