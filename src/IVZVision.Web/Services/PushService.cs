using System.Security.Cryptography;
using System.Text;
using IVZVision.Core.Configuration;
using IVZVision.Data;
using Microsoft.EntityFrameworkCore;
using WebPush;

namespace IVZVision.Web.Services;

/// <summary>
/// Envío de avisos push a los dispositivos suscritos (la aplicación instalada en el
/// teléfono). Genera las claves VAPID la primera vez, firma cada envío y da de baja
/// solas las suscripciones que el servicio de push declara muertas.
/// </summary>
public sealed class PushService
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly IConfigStore _config;
    private readonly ILogger<PushService> _logger;
    private readonly SemaphoreSlim _keysGate = new(1, 1);

    public PushService(IDbContextFactory<VisionDbContext> dbFactory, IConfigStore config,
                       ILogger<PushService> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>Clave pública para que el navegador se suscriba; genera el par si aún no existe.</summary>
    public async Task<string> GetPublicKeyAsync(CancellationToken ct = default)
    {
        var push = _config.Current.Push;
        if (!string.IsNullOrEmpty(push.VapidPublicKey)) return push.VapidPublicKey;

        await _keysGate.WaitAsync(ct);
        try
        {
            push = _config.Current.Push;
            if (!string.IsNullOrEmpty(push.VapidPublicKey)) return push.VapidPublicKey;

            var keys = VapidHelper.GenerateVapidKeys();
            var updated = await _config.UpdateAsync(cfg =>
            {
                cfg.Push.VapidPublicKey = keys.PublicKey;
                cfg.Push.VapidPrivateKey = keys.PrivateKey;
            }, ct);

            _logger.LogInformation("Claves VAPID generadas para los avisos push");
            return updated.Push.VapidPublicKey;
        }
        finally
        {
            _keysGate.Release();
        }
    }

    /// <summary>
    /// Ficha con la que el navegador puede pedir la foto de un evento sin sesión (las
    /// notificaciones cargan su imagen fuera de la aplicación). No es adivinable: sale
    /// de la clave privada del servidor y del propio número de evento.
    /// </summary>
    public string PhotoToken(long eventId)
    {
        var secreto = _config.Current.Push.VapidPrivateKey;
        if (string.IsNullOrEmpty(secreto)) return "";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secreto));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(eventId.ToString()));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    public bool ValidatePhotoToken(long eventId, string? token)
        => !string.IsNullOrEmpty(token)
           && string.Equals(PhotoToken(eventId), token, StringComparison.OrdinalIgnoreCase);

    /// <summary>Envía el aviso a todos los dispositivos suscritos, en segundo plano.</summary>
    public void SendToAll(string titulo, string cuerpo, string url, string? icono, string? imagen, string? tag)
    {
        _ = Task.Run(() => SendToAllAsync(titulo, cuerpo, url, icono, imagen, tag));
    }

    private async Task SendToAllAsync(string titulo, string cuerpo, string url, string? icono,
                                      string? imagen, string? tag)
    {
        try
        {
            var push = _config.Current.Push;
            if (!push.Enabled || string.IsNullOrEmpty(push.VapidPrivateKey)) return;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var subs = await db.PushSubscriptions.AsNoTracking().ToListAsync();
            if (subs.Count == 0) return;

            var vapid = new VapidDetails(push.VapidSubject, push.VapidPublicKey, push.VapidPrivateKey);
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                titulo, cuerpo, url, icono, imagen, tag,
            });

            using var cliente = new WebPushClient();
            var muertas = new List<int>();

            foreach (var s in subs)
            {
                try
                {
                    var destino = new PushSubscription(s.Endpoint, s.P256dh, s.Auth);
                    await cliente.SendNotificationAsync(destino, payload, vapid);
                }
                catch (WebPushException ex) when ((int)ex.StatusCode is 404 or 410)
                {
                    // El servicio de push declara la suscripción muerta: se retira.
                    muertas.Add(s.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "No se pudo enviar el aviso push a {Endpoint}",
                                     s.Endpoint.Length > 60 ? s.Endpoint[..60] : s.Endpoint);
                }
            }

            if (muertas.Count > 0)
            {
                await db.PushSubscriptions.Where(s => muertas.Contains(s.Id)).ExecuteDeleteAsync();
                _logger.LogInformation("Suscripciones push retiradas por muertas: {Count}", muertas.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo enviando avisos push");
        }
    }
}
