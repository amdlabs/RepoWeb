using System.Security.Cryptography;

namespace IVZVision.Core.Configuration;

/// <summary>Token de acceso a la API REST y al servidor MCP.</summary>
public sealed class ApiToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Para qué es el token: "Panel de control", "Asistente IA"…</summary>
    public string Name { get; set; } = "Integración";

    public string Value { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Permite consultar lo que ven las cámaras (<c>/api/ver</c> y herramientas MCP de lectura).</summary>
    public bool AllowRead { get; set; } = true;

    /// <summary>Permite obtener imágenes (instantáneas y recortes).</summary>
    public bool AllowImages { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Muestra sólo los últimos caracteres, para poder identificarlo sin exponerlo.</summary>
    public string Masked => Value.Length <= 8 ? "••••" : $"••••{Value[^6..]}";

    public static string GenerateValue()
    {
        // 32 bytes en base64 URL-safe: suficiente entropía y cómodo de pegar en una cabecera.
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "ivz_" + Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

/// <summary>Configuración de la API REST y del servidor MCP.</summary>
public sealed class ApiConfig
{
    /// <summary>Habilita <c>/api/ver</c>.</summary>
    public bool RestEnabled { get; set; } = true;

    /// <summary>Habilita el punto de conexión MCP en <c>/mcp</c>.</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>
    /// Exige token también en MCP. Desactívelo sólo si el punto de conexión está
    /// protegido por otro medio (red aislada, pasarela con autenticación…).
    /// </summary>
    public bool McpRequiresToken { get; set; } = true;

    public List<ApiToken> Tokens { get; set; } = new();

    /// <summary>Orígenes permitidos para llamadas desde el navegador (vacío = ninguno).</summary>
    public List<string> AllowedCorsOrigins { get; set; } = new();

    /// <summary>Máximo de peticiones por minuto y token (0 = sin límite).</summary>
    public int RequestsPerMinute { get; set; } = 120;
}
