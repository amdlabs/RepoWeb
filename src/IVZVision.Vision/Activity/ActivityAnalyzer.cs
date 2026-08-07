using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;

namespace IVZVision.Vision.Activity;

/// <summary>Alerta de comportamiento con el objeto que la provocó.</summary>
public sealed record ActivityAlert(ActivityKind Kind, AlertSeverity Severity, string Explanation,
                                   BoxF Box, int TrackId, string ClassName);

/// <summary>
/// Aplica las reglas de actividad sospechosa sobre los objetos seguidos.
///
/// Las reglas son deliberadamente explícitas y auditables: cada alerta dice qué
/// condición se cumplió y con qué valores. No hay un modelo de "comportamiento
/// sospechoso" que decida por su cuenta, porque eso no sería ni explicable ni
/// defendible ante quien tiene que revisar las alertas.
/// </summary>
public sealed class ActivityAnalyzer
{
    private static readonly HashSet<string> AnimalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dog", "cat", "bird", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe",
    };

    private const string PersonClass = "person";

    private DateTimeOffset _lastCrowdAlert = DateTimeOffset.MinValue;

    /// <summary>
    /// Evalúa las reglas sobre los objetos vistos en este fotograma.
    /// </summary>
    /// <param name="tracks">Objetos seguidos que se acaban de ver.</param>
    /// <param name="faces">Rostros detectados en el mismo fotograma, para la regla de rostro oculto.</param>
    /// <param name="restrictedZone">Zona restringida en píxeles, o null si la cámara no tiene.</param>
    public IReadOnlyList<ActivityAlert> Evaluate(
        IReadOnlyList<Track> tracks,
        IReadOnlyList<BoxF> faces,
        BoxF? restrictedZone,
        int frameWidth,
        DateTimeOffset now,
        ActivityConfig config)
    {
        var alerts = new List<ActivityAlert>();
        if (tracks.Count == 0) return alerts;

        var cooldown = TimeSpan.FromSeconds(Math.Max(1, config.AlertCooldownSeconds));
        var people = tracks.Where(t => IsPerson(t.ClassName)).ToList();

        // ---- Aglomeración: es de escena, no de un objeto concreto ----------
        if (config.CrowdEnabled && people.Count >= Math.Max(2, config.CrowdMinPeople)
            && now - _lastCrowdAlert >= cooldown)
        {
            _lastCrowdAlert = now;
            var box = Envelope(people.Select(p => p.Box));

            alerts.Add(new ActivityAlert(ActivityKind.Crowd, AlertSeverity.Warning,
                $"{people.Count} personas simultáneas (umbral {config.CrowdMinPeople}).",
                box, people[0].Id, PersonClass));
        }

        foreach (var track in tracks)
        {
            var isPerson = IsPerson(track.ClassName);
            var isAnimal = AnimalClasses.Contains(track.ClassName);

            // Las reglas de comportamiento sólo aplican a seres vivos.
            if (!isPerson && !isAnimal) continue;

            if (isPerson) UpdateFaceVisibility(track, faces, now);

            // ---- Merodeo -----------------------------------------------------
            if (config.LoiteringEnabled && isPerson)
            {
                var seconds = track.Age(now).TotalSeconds;
                if (seconds >= config.LoiteringSeconds && CanAlert(track, ActivityKind.Loitering, now, cooldown))
                {
                    alerts.Add(Emit(track, ActivityKind.Loitering, AlertSeverity.Warning,
                        $"Persona en la escena desde hace {seconds:0} s (umbral {config.LoiteringSeconds} s).", now));
                }
            }

            // ---- Intrusión en zona restringida ---------------------------------
            if (config.IntrusionEnabled && restrictedZone is BoxF zone
                && zone.Contains(track.Box, minOverlap: 0.35f)
                && CanAlert(track, ActivityKind.Intrusion, now, cooldown))
            {
                alerts.Add(Emit(track, ActivityKind.Intrusion, AlertSeverity.Critical,
                    $"{Describe(track.ClassName)} dentro de la zona restringida.", now));
            }

            // ---- Carrera o movimiento brusco ------------------------------------
            if (config.RunningEnabled && frameWidth > 0)
            {
                var speed = track.SpeedPixelsPerSecond();
                var limit = frameWidth * config.RunningSpeedFactor;

                if (speed > limit && CanAlert(track, ActivityKind.Running, now, cooldown))
                {
                    alerts.Add(Emit(track, ActivityKind.Running, AlertSeverity.Warning,
                        $"Desplazamiento de {speed:0} px/s, por encima del límite de {limit:0} px/s.", now));
                }
            }

            // ---- Presencia fuera de horario --------------------------------------
            if (config.ScheduleEnabled && IsOutsideSchedule(now, config)
                && CanAlert(track, ActivityKind.OutOfSchedule, now, cooldown))
            {
                alerts.Add(Emit(track, ActivityKind.OutOfSchedule, AlertSeverity.Critical,
                    $"{Describe(track.ClassName)} detectada a las {now.LocalDateTime:HH:mm}, " +
                    $"fuera del horario permitido ({config.AllowedFromHour:00}:00-{config.AllowedToHour:00}:00).", now));
            }

            // ---- Animal --------------------------------------------------------
            if (config.AnimalEnabled && isAnimal && CanAlert(track, ActivityKind.Animal, now, cooldown))
            {
                alerts.Add(Emit(track, ActivityKind.Animal, AlertSeverity.Info,
                    $"Animal detectado: {track.ClassName}.", now));
            }

            // ---- Rostro no visible ----------------------------------------------
            if (config.CoveredFaceEnabled && isPerson)
            {
                var visibleSeconds = track.Age(now).TotalSeconds;
                var faceEverSeen = track.LastFaceSeen is not null;

                if (!faceEverSeen && visibleSeconds >= config.CoveredFaceSeconds
                    && CanAlert(track, ActivityKind.CoveredFace, now, cooldown))
                {
                    alerts.Add(Emit(track, ActivityKind.CoveredFace, AlertSeverity.Warning,
                        $"Persona visible {visibleSeconds:0} s sin que se le detecte el rostro.", now));
                }
            }
        }

        return alerts;
    }

    /// <summary>Convierte la zona restringida configurada en porcentaje a píxeles.</summary>
    public static BoxF? ResolveRestrictedZone(CameraConfig camera, int frameWidth, int frameHeight)
    {
        if (!camera.RestrictedZoneEnabled) return null;

        var x = (float)(frameWidth * Math.Clamp(camera.RestrictedXPercent, 0, 100) / 100.0);
        var y = (float)(frameHeight * Math.Clamp(camera.RestrictedYPercent, 0, 100) / 100.0);
        var w = (float)(frameWidth * Math.Clamp(camera.RestrictedWidthPercent, 1, 100) / 100.0);
        var h = (float)(frameHeight * Math.Clamp(camera.RestrictedHeightPercent, 1, 100) / 100.0);

        return new BoxF(x, y, w, h).ClampTo(frameWidth, frameHeight);
    }

    private static void UpdateFaceVisibility(Track track, IReadOnlyList<BoxF> faces, DateTimeOffset now)
    {
        foreach (var face in faces)
        {
            // La cara tiene que caer dentro del cuadro de la persona para ser suya.
            if (track.Box.Contains(face, minOverlap: 0.7f))
            {
                track.LastFaceSeen = now;
                return;
            }
        }
    }

    private static bool IsOutsideSchedule(DateTimeOffset now, ActivityConfig config)
    {
        var hour = now.LocalDateTime.Hour;
        var from = Math.Clamp(config.AllowedFromHour, 0, 23);
        var to = Math.Clamp(config.AllowedToHour, 0, 23);

        // Un horario que cruza la medianoche (22-6) se interpreta al revés.
        return from <= to
            ? hour < from || hour >= to
            : hour < from && hour >= to;
    }

    private static bool CanAlert(Track track, ActivityKind kind, DateTimeOffset now, TimeSpan cooldown)
    {
        if (track.LastAlerts.TryGetValue(kind, out var last) && now - last < cooldown)
            return false;

        track.LastAlerts[kind] = now;
        return true;
    }

    private static ActivityAlert Emit(Track track, ActivityKind kind, AlertSeverity severity,
                                      string explanation, DateTimeOffset now)
        => new(kind, severity, explanation, track.Box, track.Id, track.ClassName);

    private static bool IsPerson(string className) =>
        string.Equals(className, PersonClass, StringComparison.OrdinalIgnoreCase);

    private static string Describe(string className) =>
        IsPerson(className) ? "Persona" : AnimalClasses.Contains(className) ? "Animal" : className;

    private static BoxF Envelope(IEnumerable<BoxF> boxes)
    {
        var list = boxes.ToList();
        if (list.Count == 0) return new BoxF(0, 0, 0, 0);

        var minX = list.Min(b => b.X);
        var minY = list.Min(b => b.Y);
        var maxX = list.Max(b => b.Right);
        var maxY = list.Max(b => b.Bottom);

        return new BoxF(minX, minY, maxX - minX, maxY - minY);
    }
}
