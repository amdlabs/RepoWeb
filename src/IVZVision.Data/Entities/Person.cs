using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

/// <summary>Persona conocida por el sistema.</summary>
public class Person
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string FullName { get; set; } = "";

    [MaxLength(50)]
    public string? DocumentId { get; set; }

    [MaxLength(100)]
    public string? Department { get; set; }

    /// <summary>Si es false, al detectarla el evento se marca como "no autorizada".</summary>
    public bool IsAuthorized { get; set; } = true;

    public bool IsActive { get; set; } = true;

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<FaceTemplate> FaceTemplates { get; set; } = new();
    public List<Vehicle> Vehicles { get; set; } = new();
}
