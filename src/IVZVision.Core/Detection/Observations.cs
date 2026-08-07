namespace IVZVision.Core.Detection;

public enum ObservationKind
{
    Face = 0,
    Plate = 1,
}

/// <summary>Cuadro delimitador en píxeles del fotograma original.</summary>
public readonly record struct BoxF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float Area => Math.Max(0, Width) * Math.Max(0, Height);

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
    public string Label { get; init; } = "Desconocido";
    public float Score { get; init; }
    public bool IsAuthorized { get; init; }
    public string? Notes { get; init; }

    public static readonly IdentityMatch Unknown = new() { IsKnown = false, Label = "Desconocido" };
}

/// <summary>Un rostro o matrícula localizado en un fotograma, ya contrastado con la base de datos.</summary>
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

    /// <summary>Texto de la matrícula normalizado (solo para <see cref="ObservationKind.Plate"/>).</summary>
    public string? PlateText { get; init; }

    /// <summary>Confianza del OCR (0-1).</summary>
    public float? OcrConfidence { get; init; }

    public IdentityMatch Match { get; init; } = IdentityMatch.Unknown;

    /// <summary>Recorte JPEG del sujeto, en base64, para mostrarlo en la web.</summary>
    public string? CropJpegBase64 { get; set; }

    /// <summary>Ruta del recorte en disco si se guardó.</summary>
    public string? CropPath { get; set; }

    /// <summary>Id del evento generado en base de datos, si se registró.</summary>
    public long? EventId { get; set; }

    public string DisplayLabel => Kind == ObservationKind.Plate
        ? (Match.IsKnown ? $"{PlateText} · {Match.Label}" : PlateText ?? "?")
        : Match.Label;
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
    public string RtspUrlMasked { get; set; } = "";
}
