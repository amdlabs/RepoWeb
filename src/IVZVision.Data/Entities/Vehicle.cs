using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

/// <summary>Vehículo conocido, identificado por su matrícula normalizada.</summary>
public class Vehicle
{
    public int Id { get; set; }

    /// <summary>Matrícula normalizada (mayúsculas, sin separadores). Es la clave de búsqueda.</summary>
    [Required, MaxLength(20)]
    public string Plate { get; set; } = "";

    /// <summary>Matrícula tal y como la escribió el usuario.</summary>
    [MaxLength(30)]
    public string? PlateRaw { get; set; }

    [MaxLength(100)]
    public string? Make { get; set; }

    [MaxLength(100)]
    public string? Model { get; set; }

    [MaxLength(50)]
    public string? Color { get; set; }

    public int? OwnerPersonId { get; set; }
    public Person? OwnerPerson { get; set; }

    public bool IsAuthorized { get; set; } = true;

    public bool IsActive { get; set; } = true;

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
