using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

/// <summary>Embedding facial (vector de características) asociado a una persona.</summary>
public class FaceTemplate
{
    public int Id { get; set; }

    public int PersonId { get; set; }
    public Person? Person { get; set; }

    /// <summary>Vector float32 serializado en little-endian.</summary>
    [Required]
    public byte[] Embedding { get; set; } = Array.Empty<byte>();

    /// <summary>Número de dimensiones del vector (128 en SFace).</summary>
    public int Dimensions { get; set; }

    /// <summary>Identificador del modelo con el que se generó, para invalidar al cambiarlo.</summary>
    [MaxLength(120)]
    public string ModelId { get; set; } = "";

    /// <summary>Ruta relativa de la imagen de referencia, si se conservó.</summary>
    [MaxLength(400)]
    public string? ImagePath { get; set; }

    public float Quality { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
