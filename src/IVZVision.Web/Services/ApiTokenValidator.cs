using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using IVZVision.Core.Configuration;

namespace IVZVision.Web.Services;

public sealed record TokenCheck(bool Ok, string? Error, ApiToken? Token = null);

/// <summary>
/// Valida el token de uso de la API. Acepta la cabecera <c>X-API-Token</c>,
/// <c>Authorization: Bearer …</c> o el parámetro <c>?token=</c>, para que sea cómodo
/// tanto desde un script como desde un navegador.
/// </summary>
public sealed class ApiTokenValidator
{
    private readonly IConfigStore _config;
    private readonly ConcurrentDictionary<string, RateWindow> _rates = new();

    public ApiTokenValidator(IConfigStore config) => _config = config;

    public TokenCheck Validate(HttpRequest request, bool requireImages = false)
    {
        var api = _config.Current.Api;

        var presented = Extract(request);
        if (string.IsNullOrWhiteSpace(presented))
            return new TokenCheck(false, "Falta el token. Envíelo en la cabecera X-API-Token, " +
                                          "en Authorization: Bearer o en el parámetro ?token=.");

        // Comparación en tiempo constante contra todos los tokens: así el tiempo de
        // respuesta no revela cuántos caracteres del token son correctos.
        ApiToken? matched = null;
        foreach (var candidate in api.Tokens)
        {
            if (!candidate.Enabled || string.IsNullOrEmpty(candidate.Value)) continue;
            if (FixedTimeEquals(candidate.Value, presented)) matched = candidate;
        }

        if (matched is null) return new TokenCheck(false, "Token no válido o desactivado.");
        if (!matched.AllowRead) return new TokenCheck(false, "El token no tiene permiso de lectura.");
        if (requireImages && !matched.AllowImages)
            return new TokenCheck(false, "El token no tiene permiso para obtener imágenes.");

        if (!WithinRateLimit(matched, api.RequestsPerMinute))
            return new TokenCheck(false, $"Se ha superado el límite de {api.RequestsPerMinute} peticiones por minuto.");

        matched.LastUsedAt = DateTimeOffset.UtcNow;
        return new TokenCheck(true, null, matched);
    }

    private static string? Extract(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-API-Token", out var header) && header.Count > 0)
            return header[0];

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization[7..].Trim();

        if (request.Query.TryGetValue("token", out var query) && query.Count > 0)
            return query[0];

        return null;
    }

    private static bool FixedTimeEquals(string expected, string presented)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(presented);

        // CryptographicOperations.FixedTimeEquals exige la misma longitud, así que
        // se comparan resúmenes de longitud fija.
        Span<byte> ha = stackalloc byte[32];
        Span<byte> hb = stackalloc byte[32];
        SHA256.HashData(a, ha);
        SHA256.HashData(b, hb);

        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }

    private bool WithinRateLimit(ApiToken token, int perMinute)
    {
        if (perMinute <= 0) return true;

        var now = DateTimeOffset.UtcNow;
        var window = _rates.GetOrAdd(token.Id, _ => new RateWindow());

        lock (window)
        {
            if (now - window.Start >= TimeSpan.FromMinutes(1))
            {
                window.Start = now;
                window.Count = 0;
            }

            window.Count++;
            return window.Count <= perMinute;
        }
    }

    private sealed class RateWindow
    {
        public DateTimeOffset Start { get; set; } = DateTimeOffset.UtcNow;
        public int Count { get; set; }
    }
}
