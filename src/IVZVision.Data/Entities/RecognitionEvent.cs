using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

public enum RecognitionKind
{
    Face = 0,
    Plate = 1,
    Object = 2,
    Text = 3,
}

public enum RecognitionSource
{
    /// <summary>Reconocido localmente por el motor de la aplicación.</summary>
    Local = 0,
    /// <summary>Recibido de la propia cámara vía ISAPI (ANPR embebido de Hikvision).</summary>
    CameraEvent = 1,
}

/// <summary>Registro histórico de cada rostro o matrícula reconocidos.</summary>
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

    /// <summary>Texto mostrado: nombre de la persona o matrícula leída.</summary>
    [MaxLength(200)]
    public string Label { get; set; } = "";

    [MaxLength(20)]
    public string? PlateText { get; set; }

    public float? OcrConfidence { get; set; }

    /// <summary>Clase del objeto detectado (solo para <see cref="RecognitionKind.Object"/>).</summary>
    [MaxLength(60)]
    public string? ObjectClass { get; set; }

    // Cuadrante dentro del fotograma
    public int BoxX { get; set; }
    public int BoxY { get; set; }
    public int BoxWidth { get; set; }
    public int BoxHeight { get; set; }

    /// <summary>Ruta relativa del recorte guardado en disco.</summary>
    [MaxLength(400)]
    public string? CropPath { get; set; }

    /// <summary>Recorte JPEG del sujeto codificado en base64, guardado en la propia base de datos.</summary>
    public string? CropBase64 { get; set; }

    /// <summary>Grupo de rostros al que pertenece esta detección (agrupa la misma cara entre cámaras).</summary>
    public int? FaceClusterId { get; set; }

    /// <summary>Ruta relativa del fotograma completo (la escena entera) de esta detección.</summary>
    [MaxLength(400)]
    public string? FullFramePath { get; set; }
}
