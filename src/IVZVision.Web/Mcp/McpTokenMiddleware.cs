using IVZVision.Core.Configuration;
using IVZVision.Web.Services;

namespace IVZVision.Web.Mcp;

/// <summary>
/// Protege el punto de conexión MCP con el mismo token que la API REST.
/// Se puede desactivar desde la configuración cuando el acceso ya está limitado
/// por otro medio (red aislada o pasarela con autenticación propia).
/// </summary>
public sealed class McpTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfigStore _config;
    private readonly ApiTokenValidator _tokens;

    public McpTokenMiddleware(RequestDelegate next, IConfigStore config, ApiTokenValidator tokens)
    {
        _next = next;
        _config = config;
        _tokens = tokens;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var api = _config.Current.Api;

        if (!api.McpEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "El servidor MCP está desactivado en la configuración." });
            return;
        }

        if (api.McpRequiresToken)
        {
            var check = _tokens.Validate(context.Request);
            if (!check.Ok)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                // Indica al cliente cómo autenticarse, como manda el protocolo HTTP.
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"IVZ Vision MCP\"";
                await context.Response.WriteAsJsonAsync(new { error = check.Error });
                return;
            }
        }

        await _next(context);
    }
}
