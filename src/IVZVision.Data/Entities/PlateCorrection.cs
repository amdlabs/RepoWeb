using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

/// <summary>
/// Corrección de una lectura de matrícula hecha por el usuario. El motor aprende de
/// ellas: cuando el OCR vuelve a leer el mismo texto erróneo (o uno muy parecido)
/// se sustituye por el corregido, y el evento original queda ya rectificado.
/// </summary>
public class PlateCorrection
{
    public int Id { get; set; }

    /// <summary>Texto que leyó el OCR, normalizado.</summary>
    [MaxLength(20)]
    public string WrongText { get; set; } = "";

    /// <summary>Texto correcto indicado por el usuario, normalizado.</summary>
    [MaxLength(20)]
    public string CorrectText { get; set; } = "";

    /// <summary>Veces que se ha aplicado esta corrección (indica su utilidad).</summary>
    public int TimesApplied { get; set; }

    [MaxLength(150)]
    public string? CorrectedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
