namespace IVZVision.Core.Detection;

public enum ObservationKind
{
    Face = 0,
    Plate = 1,
    /// <summary>Objeto del detector multiclase: persona, animal, vehículo, mochila…</summary>
    Object = 2,
    /// <summary>Código QR o de barras.</summary>
    Code = 3,
    /// <summary>Línea de texto o escritura leída en la escena.</summary>
    Text = 4,
    /// <summary>Actividad sospechosa detectada por las reglas de comportamiento.</summary>
    Activity = 5,
}

/// <summary>Tipos de alerta de comportamiento. Cada uno corresponde a una regla explícita.</summary>
public enum ActivityKind
{
    None = 0,
    /// <summary>Permanencia prolongada en la escena.</summary>
    Loitering = 1,
    /// <summary>Entrada en la zona restringida de la cámara.</summary>
    Intrusion = 2,
    /// <summary>Más personas de las permitidas a la vez.</summary>
    Crowd = 3,
    /// <summary>Desplazamiento muy rápido (carrera o huida).</summary>
    Running = 4,
    /// <summary>Presencia fuera del horario permitido.</summary>
    OutOfSchedule = 5,
    /// <summary>Animal en la escena.</summary>
    Animal = 6,
    /// <summary>Persona visible durante varios segundos sin que se le vea la cara.</summary>
    CoveredFace = 7,
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>Cuadro delimitador en píxeles del fotograma original.</summary>
public readonly record struct BoxF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float Area => Math.Max(0, Width) * Math.Max(0, Height);
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;

    public BoxF ClampTo(int frameWidth, int frameHeight)
    {
        var x = Math.Clamp(X, 0, frameWidth);
        var y = Math.Clamp(Y, 0, frameHeight);
        var w = Math.Clamp(Width, 0, frameWidth - x);
        var h = Math.Clamp(Height, 0, frameHeight - y);
        return new BoxF(x, y, w, h);
    }

    /// <summary>Amplía el cuadro un porcentaje por cada lado (para recortar con margen).</summary>
    public BoxF Expand(float ratio, int frameWidth, int frameHeight)
    {
        var dx = Width * ratio;
        var dy = Height * ratio;
        return new BoxF(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy).ClampTo(frameWidth, frameHeight);
    }

    /// <summary>true si <paramref name="other"/> queda mayoritariamente dentro de este cuadro.</summary>
    public bool Contains(BoxF other, float minOverlap = 0.6f)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(Right, other.Right);
        var y2 = Math.Min(Bottom, other.Bottom);

        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        return other.Area > 0 && inter / other.Area >= minOverlap;
    }

    public static float IntersectionOverUnion(BoxF a, BoxF b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);

        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        if (inter <= 0) return 0;

        var union = a.Area + b.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }
}

/// <summary>Resultado de comparar un sujeto detectado contra la base de datos.</summary>
public sealed class IdentityMatch
{
    public bool IsKnown { get; init; }
    public int? PersonId { get; init; }
    public int? VehicleId { get; init; }
    public int? ObjectId { get; init; }
    public string Label { get; init; } = "Desconocido";
    public float Score { get; init; }
    public bool IsAuthorized { get; init; }
    public string? Notes { get; init; }

    public static readonly IdentityMatch Unknown = new() { IsKnown = false, Label = "Desconocido" };
}

/// <summary>Algo localizado en un fotograma, ya contrastado con la base de datos.</summary>
public sealed class Observation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ObservationKind Kind { get; init; }
    public Guid CameraId { get; init; }
    public string CameraName { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public BoxF Box { get; init; }

    /// <summary>Confianza del detector (0-1).</summary>
    public float DetectionScore { get; init; }

    /// <summary>Texto de la matrícula normalizado (sólo para <see cref="ObservationKind.Plate"/>).</summary>
    public string? PlateText { get; init; }

    /// <summary>Confianza del OCR (0-1), tanto para matrículas como para texto.</summary>
    public float? OcrConfidence { get; init; }

    /// <summary>Clase del detector de objetos (person, dog, car…).</summary>
    public string? ObjectClass { get; init; }

    /// <summary>Contenido del código QR o de barras.</summary>
    public string? CodeValue { get; init; }

    /// <summary>Formato del código: QR_CODE, EAN_13, CODE_128…</summary>
    public string? CodeFormat { get; init; }

    /// <summary>Texto leído de la escena.</summary>
    public string? TextValue { get; init; }

    /// <summary>Tipo de actividad sospechosa (sólo para <see cref="ObservationKind.Activity"/>).</summary>
    public ActivityKind Activity { get; init; }

    public AlertSeverity Severity { get; init; } = AlertSeverity.Info;

    /// <summary>Explicación de por qué se ha disparado la alerta.</summary>
    public string? Explanation { get; init; }

    /// <summary>Identificador del objeto seguido entre fotogramas, cuando aplica.</summary>
    public int? TrackId { get; init; }

    /// <summary>Vector de características del sujeto, para poder aprenderlo si se le pone nombre.</summary>
    public float[]? Embedding { get; init; }

    public IdentityMatch Match { get; init; } = IdentityMatch.Unknown;

    /// <summary>Recorte JPEG del sujeto, en base64, para mostrarlo en la web.</summary>
    public string? CropJpegBase64 { get; set; }

    /// <summary>Ruta del recorte en disco si se guardó.</summary>
    public string? CropPath { get; set; }

    /// <summary>Id del evento generado en base de datos, si se registró.</summary>
    public long? EventId { get; set; }

    public string DisplayLabel => Kind switch
    {
        ObservationKind.Plate => Match.IsKnown ? $"{PlateText} · {Match.Label}" : PlateText ?? "?",
        ObservationKind.Code => $"{CodeFormat}: {Truncate(CodeValue, 40)}",
        ObservationKind.Text => Truncate(TextValue, 48) ?? "",
        ObservationKind.Object => Match.IsKnown ? Match.Label : (ObjectClass ?? "objeto"),
        ObservationKind.Activity => DescribeActivity(Activity),
        _ => Match.Label,
    };

    /// <summary>Contenido buscable del sujeto, usado por el buscador por texto.</summary>
    public string SearchableText => string.Join(' ', new[]
    {
        Match.Label, PlateText, ObjectClass, CodeValue, TextValue, Explanation,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static string DescribeActivity(ActivityKind kind) => kind switch
    {
        ActivityKind.Loitering => "Merodeo",
        ActivityKind.Intrusion => "Intrusión en zona restringida",
        ActivityKind.Crowd => "Aglomeración",
        ActivityKind.Running => "Movimiento brusco o carrera",
        ActivityKind.OutOfSchedule => "Presencia fuera de horario",
        ActivityKind.Animal => "Animal detectado",
        ActivityKind.CoveredFace => "Persona con el rostro no visible",
        _ => "Actividad",
    };

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max] + "…";
    }
}

/// <summary>Estado publicado de una cámara para la interfaz.</summary>
public sealed class CameraStatus
{
    public Guid CameraId { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Connected { get; set; }
    public string State { get; set; } = "Detenida";
    public string? LastError { get; set; }
    public DateTimeOffset? LastFrameAt { get; set; }
    public double MeasuredFps { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    public long FramesProcessed { get; set; }

    /// <summary>Origen legible y sin credenciales: URL RTSP enmascarada o dispositivo USB.</summary>
    public string SourceDescription { get; set; } = "";
}
