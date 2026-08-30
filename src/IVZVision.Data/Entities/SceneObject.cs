using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

/// <summary>
/// Objeto quieto que una cámara sabe que tiene delante (unos libros, una caja, una
/// escalera). Se aprende la primera vez que se ve y deja de anunciarse mientras siga
/// en su sitio; el sistema anota cuándo se le vio por última vez, junto a qué otras
/// cosas estaba, y avisa si desaparece de la escena o vuelve a ella.
/// </summary>
public class SceneObject
{
    public int Id { get; set; }

    public Guid CameraId { get; set; }

    [MaxLength(60)]
    public string ObjectClass { get; set; } = "";

    // Posición y tamaño en porcentaje del fotograma: independientes de la resolución.
    public double XPercent { get; set; }
    public double YPercent { get; set; }
    public double WidthPercent { get; set; }
    public double HeightPercent { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Veces que se le ha visto en su sitio (cuanto mayor, más «mueble» es).</summary>
    public int TimesSeen { get; set; } = 1;

    /// <summary>false cuando desapareció de la escena y todavía no ha vuelto.</summary>
    public bool IsPresent { get; set; } = true;

    /// <summary>Clases junto a las que estaba la última vez que se le vio.</summary>
    [MaxLength(400)]
    public string? LastNeighbors { get; set; }

    /// <summary>Último recorte del objeto, para poder enseñarlo si desaparece.</summary>
    public string? CropBase64 { get; set; }
}
