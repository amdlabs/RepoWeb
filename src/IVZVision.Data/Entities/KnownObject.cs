using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

/// <summary>
/// Objeto al que se le ha puesto nombre ("Carretilla del almacén", "Mochila de Ana").
/// Si hay un extractor de características de objetos configurado, se guarda además su
/// vector y el sistema lo reconoce por su apariencia; si no, queda como catálogo y
/// como conjunto etiquetado que se puede exportar para reentrenar el detector.
/// </summary>
public class KnownObject
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = "";

    /// <summary>Clase del detector a la que pertenece (person, backpack, car…).</summary>
    [MaxLength(80)]
    public string? ObjectClass { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public bool IsAuthorized { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public int? OwnerPersonId { get; set; }
    public Person? OwnerPerson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ObjectTemplate> Templates { get; set; } = new();
}

/// <summary>Vector de características de una muestra de un objeto conocido.</summary>
public class ObjectTemplate
{
    public int Id { get; set; }

    public int KnownObjectId { get; set; }
    public KnownObject? KnownObject { get; set; }

    [Required]
    public byte[] Embedding { get; set; } = Array.Empty<byte>();

    public int Dimensions { get; set; }

    [MaxLength(120)]
    public string ModelId { get; set; } = "";

    [MaxLength(400)]
    public string? ImagePath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
