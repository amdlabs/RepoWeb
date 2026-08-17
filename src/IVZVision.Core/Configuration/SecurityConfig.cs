namespace IVZVision.Core.Configuration;

/// <summary>Ajustes de seguridad de la aplicación.</summary>
public sealed class SecurityConfig
{
    /// <summary>
    /// Clave para las integraciones externas con la API JSON (cabecera <c>X-Api-Key</c>).
    /// Los usuarios con sesión iniciada no la necesitan. Si se deja vacía, la API
    /// sólo responde a sesiones iniciadas (recomendado si el sitio es público).
    /// </summary>
    public string ApiKey { get; set; } = "";
}
