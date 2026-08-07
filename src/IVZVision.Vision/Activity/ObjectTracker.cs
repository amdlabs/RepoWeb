using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;

namespace IVZVision.Vision.Activity;

/// <summary>Objeto seguido entre fotogramas.</summary>
public sealed class Track
{
    public int Id { get; init; }
    public string ClassName { get; set; } = "";
    public BoxF Box { get; set; }
    public float Score { get; set; }

    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Fotogramas consecutivos sin volver a verlo.</summary>
    public int MissedFrames { get; set; }

    /// <summary>Últimas posiciones del centro, para estimar la velocidad.</summary>
    public List<(DateTimeOffset At, float X, float Y)> Trail { get; } = new();

    /// <summary>Momento en que se le vio la cara por última vez (para la regla de rostro oculto).</summary>
    public DateTimeOffset? LastFaceSeen { get; set; }

    /// <summary>Última vez que se emitió cada tipo de alerta sobre este objeto.</summary>
    public Dictionary<ActivityKind, DateTimeOffset> LastAlerts { get; } = new();

    public TimeSpan Age(DateTimeOffset now) => now - FirstSeen;

    /// <summary>
    /// Velocidad del centro en píxeles por segundo, medida sobre la ventana reciente.
    /// Se usan varios puntos para que un salto puntual del detector no la dispare.
    /// </summary>
    public double SpeedPixelsPerSecond()
    {
        if (Trail.Count < 2) return 0;

        var first = Trail[0];
        var last = Trail[^1];

        var seconds = (last.At - first.At).TotalSeconds;
        if (seconds < 0.15) return 0;

        var dx = last.X - first.X;
        var dy = last.Y - first.Y;

        return Math.Sqrt(dx * dx + dy * dy) / seconds;
    }
}

/// <summary>
/// Seguimiento sencillo por solapamiento: a cada detección se le asigna el track
/// existente con el que más se solapa. Es suficiente para las reglas de actividad
/// (permanencia, velocidad, entrada en zona) y no añade dependencias.
/// </summary>
public sealed class ObjectTracker
{
    private const int TrailLength = 8;

    private readonly List<Track> _tracks = new();
    private int _nextId = 1;

    public IReadOnlyList<Track> Tracks => _tracks;

    /// <summary>Actualiza el seguimiento con las detecciones del fotograma actual.</summary>
    public IReadOnlyList<Track> Update(IReadOnlyList<(BoxF Box, float Score, string ClassName)> detections,
                                       DateTimeOffset now, ActivityConfig config)
    {
        var assigned = new bool[detections.Count];
        var updated = new List<Track>();

        // Se emparejan por IoU descendente para que las asociaciones más claras
        // se resuelvan primero.
        var pairs = new List<(float Iou, Track Track, int Index)>();

        foreach (var track in _tracks)
        {
            for (var i = 0; i < detections.Count; i++)
            {
                if (!string.Equals(track.ClassName, detections[i].ClassName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var iou = BoxF.IntersectionOverUnion(track.Box, detections[i].Box);
                if (iou >= config.TrackIouThreshold) pairs.Add((iou, track, i));
            }
        }

        var usedTracks = new HashSet<int>();

        foreach (var (_, track, index) in pairs.OrderByDescending(p => p.Iou))
        {
            if (assigned[index] || usedTracks.Contains(track.Id)) continue;

            assigned[index] = true;
            usedTracks.Add(track.Id);

            var detection = detections[index];
            track.Box = detection.Box;
            track.Score = detection.Score;
            track.LastSeen = now;
            track.MissedFrames = 0;
            PushTrail(track, now);

            updated.Add(track);
        }

        // Las detecciones sin pareja son objetos nuevos.
        for (var i = 0; i < detections.Count; i++)
        {
            if (assigned[i]) continue;

            var detection = detections[i];
            var track = new Track
            {
                Id = _nextId++,
                ClassName = detection.ClassName,
                Box = detection.Box,
                Score = detection.Score,
                FirstSeen = now,
                LastSeen = now,
            };
            PushTrail(track, now);

            _tracks.Add(track);
            updated.Add(track);
        }

        // Los tracks no vistos envejecen y acaban descartándose.
        foreach (var track in _tracks)
        {
            if (usedTracks.Contains(track.Id) || updated.Any(t => t.Id == track.Id)) continue;
            track.MissedFrames++;
        }

        _tracks.RemoveAll(t => t.MissedFrames > Math.Max(1, config.TrackMaxMissedFrames));

        return updated;
    }

    public void Reset()
    {
        _tracks.Clear();
        _nextId = 1;
    }

    private static void PushTrail(Track track, DateTimeOffset now)
    {
        track.Trail.Add((now, track.Box.CenterX, track.Box.CenterY));
        if (track.Trail.Count > TrailLength) track.Trail.RemoveAt(0);
    }
}
