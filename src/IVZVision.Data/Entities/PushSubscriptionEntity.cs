using System.ComponentModel.DataAnnotations;

namespace IVZVision.Data.Entities;

/// <summary>
/// Suscripción push de un dispositivo (el teléfono o el navegador donde se activaron
/// los avisos). El endpoint lo emite el servicio de push del navegador y es único.
/// </summary>
public class PushSubscriptionEntity
{
    public int Id { get; set; }

    [MaxLength(500)]
    public string Endpoint { get; set; } = "";

    [MaxLength(200)]
    public string P256dh { get; set; } = "";

    [MaxLength(100)]
    public string Auth { get; set; } = "";

    /// <summary>Usuario que activó los avisos en ese dispositivo.</summary>
    [MaxLength(100)]
    public string? Username { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Fallos seguidos al enviar; a partir de unos cuantos se da de baja sola.</summary>
    public int FailCount { get; set; }
}
