using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

public enum RecognitionKind
{
    Face = 0,
    Plate = 1,
    Object = 2,
    Code = 3,
    Text = 4,
    Activity = 5,
}

public enum RecognitionSource
{
    /// <summary>Reconocido localmente por el motor de la aplicación.</summary>
    Local = 0,
    /// <summary>Recibido de la propia cámara vía ISAPI (ANPR embebido de Hikvision).</summary>
    CameraEvent = 1,
}

/// <summary>Registro histórico de todo lo que el sistema ha reconocido.</summary>
public class RecognitionEvent
{
    public long Id { get; set; }

    public Guid CameraId { get; set; }

    [MaxLength(150)]
    public string CameraName { get; set; } = "";

    public RecognitionKind Kind { get; set; }

    public RecognitionSource Source { get; set; } = RecognitionSource.Local;

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Confianza del detector (0-1).</summary>
    public float DetectionScore { get; set; }

    /// <summary>Similitud con el registro de la base de datos (0-1). 0 si no se identificó.</summary>
    public float MatchScore { get; set; }

    public bool IsKnown { get; set; }

    public bool IsAuthorized { get; set; }

    public int? PersonId { get; set; }
    public Person? Person { get; set; }

    public int? VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }

    public int? KnownObjectId { get; set; }
    public KnownObject? KnownObject { get; set; }

    /// <summary>Texto mostrado: nombre de la persona, matrícula, clase del objeto…</summary>
    [MaxLength(200)]
    public string Label { get; set; } = "";

    [MaxLength(20)]
    public string? PlateText { get; set; }

    public float? OcrConfidence { get; set; }

    /// <summary>Clase del detector de objetos.</summary>
    [MaxLength(80)]
    public string? ObjectClass { get; set; }

    /// <summary>Contenido del código QR o de barras.</summary>
    [MaxLength(2000)]
    public string? CodeValue { get; set; }

    [MaxLength(40)]
    public string? CodeFormat { get; set; }

    /// <summary>Texto leído de la escena.</summary>
    [MaxLength(2000)]
    public string? TextValue { get; set; }

    /// <summary>Tipo de actividad sospechosa (0 = ninguna).</summary>
    public int ActivityKind { get; set; }

    /// <summary>0 = informativa, 1 = aviso, 2 = crítica.</summary>
    public int Severity { get; set; }

    /// <summary>Explicación de por qué se disparó la alerta.</summary>
    [MaxLength(500)]
    public string? Explanation { get; set; }

    // Cuadrante dentro del fotograma
    public int BoxX { get; set; }
    public int BoxY { get; set; }
    public int BoxWidth { get; set; }
    public int BoxHeight { get; set; }

    /// <summary>Ruta relativa del recorte guardado en disco.</summary>
    [MaxLength(400)]
    public string? CropPath { get; set; }
}
