using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

public enum PendingStatus
{
    /// <summary>A la espera de que alguien le ponga nombre.</summary>
    Pending = 0,
    /// <summary>Ya se le asignó una identidad; el sistema lo reconoce.</summary>
    Assigned = 1,
    /// <summary>Descartado a mano: no vuelve a aparecer en la lista.</summary>
    Ignored = 2,
}

/// <summary>
/// Sujeto detectado que el sistema no supo identificar. Se agrupan las apariciones
/// del mismo sujeto (por parecido facial, por texto de matrícula o por clase de
/// objeto) para que la lista sea revisable, y al asignarle un nombre pasa a formar
/// parte del padrón: a partir de ese momento se reconoce.
/// </summary>
public class PendingSubject
{
    public long Id { get; set; }

    public RecognitionKind Kind { get; set; }

    public PendingStatus Status { get; set; } = PendingStatus.Pending;

    public Guid CameraId { get; set; }

    [MaxLength(150)]
    public string CameraName { get; set; } = "";

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cuántas veces se ha vuelto a ver a este mismo sujeto.</summary>
    public int Occurrences { get; set; } = 1;

    /// <summary>
    /// Vector de características de la mejor muestra. Al asignar un nombre se
    /// convierte directamente en la plantilla de reconocimiento, sin reprocesar la imagen.
    /// </summary>
    public byte[]? Embedding { get; set; }

    public int Dimensions { get; set; }

    [MaxLength(120)]
    public string ModelId { get; set; } = "";

    /// <summary>Matrícula leída, cuando el sujeto es un vehículo.</summary>
    [MaxLength(20)]
    public string? PlateText { get; set; }

    /// <summary>Clase del detector cuando el sujeto es un objeto (person, dog, backpack…).</summary>
    [MaxLength(80)]
    public string? ObjectClass { get; set; }

    /// <summary>Mejor confianza obtenida entre todas las apariciones.</summary>
    public float BestScore { get; set; }

    /// <summary>Recorte de la mejor muestra.</summary>
    [MaxLength(400)]
    public string? CropPath { get; set; }

    /// <summary>Nombre propuesto por quien revisa, antes de confirmar.</summary>
    [MaxLength(200)]
    public string? SuggestedName { get; set; }

    public int? AssignedPersonId { get; set; }
    public Person? AssignedPerson { get; set; }

    public int? AssignedVehicleId { get; set; }
    public Vehicle? AssignedVehicle { get; set; }

    public int? AssignedObjectId { get; set; }
    public KnownObject? AssignedObject { get; set; }

    public DateTime? ResolvedAt { get; set; }
}
