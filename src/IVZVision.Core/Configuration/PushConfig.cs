namespace IVZVision.Core.Configuration;

/// <summary>
/// Avisos push a los teléfonos (Web Push sobre la aplicación instalada). Las claves
/// VAPID identifican a este servidor ante los servicios de push de Google/Mozilla y
/// se generan solas la primera vez que alguien activa los avisos.
/// </summary>
public sealed class PushConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Clave pública VAPID (la conoce el navegador al suscribirse).</summary>
    public string VapidPublicKey { get; set; } = "";

    /// <summary>Clave privada VAPID: firma cada envío. No compartir.</summary>
    public string VapidPrivateKey { get; set; } = "";

    /// <summary>Contacto exigido por el protocolo (mailto:), para avisos de los servicios de push.</summary>
    public string VapidSubject { get; set; } = "mailto:amartinez@invenzis.com";

    /// <summary>Segundos de silencio por persona y cámara antes de volver a avisar.</summary>
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>true = avisar también de rostros sin identificar.</summary>
    public bool NotifyUnknown { get; set; } = true;
}
