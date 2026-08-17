using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

/// <summary>
/// Etiqueta que el usuario asigna a una clase de objeto detectada (p. ej. «perro» → «Firulais»
/// o «camion» → «Camión de reparto»). Mientras una clase no tenga etiqueta, sus detecciones
/// se listan como objetos desconocidos; una vez etiquetada pasan a «conocidos».
/// </summary>
public class ObjectLabel
{
    public int Id { get; set; }

    /// <summary>Clase del detector, normalizada en minúsculas («persona», «coche», «perro»…).</summary>
    [MaxLength(60)]
    public string ClassName { get; set; } = "";

    /// <summary>Nombre que el usuario da a esta clase de objeto.</summary>
    [MaxLength(150)]
    public string DisplayName { get; set; } = "";

    [MaxLength(400)]
    public string? Notes { get; set; }

    /// <summary>Marca si la presencia de este objeto está autorizada o debe alertar.</summary>
    public bool IsAuthorized { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
