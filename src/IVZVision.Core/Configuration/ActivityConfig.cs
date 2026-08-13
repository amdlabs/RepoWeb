namespace IVZVision.Core.Configuration;

/// <summary>
/// Reglas de actividad sospechosa. Son deliberadamente explicables: cada alerta
/// indica qué condición se ha cumplido, en vez de salir de un modelo opaco de
/// clasificación de comportamiento.
/// </summary>
public sealed class ActivityConfig
{
    /// <summary>Merodeo: una persona permanece en la escena más de este tiempo.</summary>
    public bool LoiteringEnabled { get; set; } = true;
    public int LoiteringSeconds { get; set; } = 45;

    /// <summary>Intrusión: una persona o un animal entra en la zona restringida de la cámara.</summary>
    public bool IntrusionEnabled { get; set; } = true;

    /// <summary>Aglomeración: más de N personas a la vez en el fotograma.</summary>
    public bool CrowdEnabled { get; set; } = true;
    public int CrowdMinPeople { get; set; } = 5;

    /// <summary>
    /// Carrera o movimiento brusco: el centro del objeto se desplaza más de esta
    /// fracción del ancho del fotograma por segundo.
    /// </summary>
    public bool RunningEnabled { get; set; } = true;
    public double RunningSpeedFactor { get; set; } = 0.35;

    /// <summary>Presencia fuera del horario permitido.</summary>
    public bool ScheduleEnabled { get; set; } = false;
    public int AllowedFromHour { get; set; } = 7;
    public int AllowedToHour { get; set; } = 21;

    /// <summary>Avisa cuando se detecta un animal.</summary>
    public bool AnimalEnabled { get; set; } = true;

    /// <summary>
    /// Rostro oculto: se ve a una persona durante varios segundos y en ningún
    /// momento se detecta su cara (casco, pasamontañas, espalda a la cámara).
    /// </summary>
    public bool CoveredFaceEnabled { get; set; } = true;
    public int CoveredFaceSeconds { get; set; } = 8;

    /// <summary>Segundos durante los que no se repite la misma alerta sobre el mismo objeto.</summary>
    public int AlertCooldownSeconds { get; set; } = 60;

    /// <summary>Fotogramas seguidos sin ver un objeto antes de dar su seguimiento por terminado.</summary>
    public int TrackMaxMissedFrames { get; set; } = 12;

    /// <summary>IoU mínimo para considerar que dos detecciones son el mismo objeto.</summary>
    public float TrackIouThreshold { get; set; } = 0.3f;
}
