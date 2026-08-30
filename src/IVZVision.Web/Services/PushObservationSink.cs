using System.Collections.Concurrent;
using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Pipeline;

namespace IVZVision.Web.Services;

/// <summary>
/// Convierte cada rostro registrado en un aviso push al teléfono: nombre (o «rostro
/// desconocido»), cámara, hora y la foto de la cara. La misma persona en la misma
/// cámara no repite aviso hasta pasado el tiempo de silencio configurado.
/// </summary>
public sealed class PushObservationSink : IObservationSink
{
    private readonly PushService _push;
    private readonly IConfigStore _config;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _ultimos = new();

    public PushObservationSink(PushService push, IConfigStore config)
    {
        _push = push;
        _config = config;
    }

    public Task OnObservationAsync(Observation obs, CancellationToken ct = default)
    {
        var push = _config.Current.Push;
        if (!push.Enabled) return Task.CompletedTask;
        if (obs.Kind != ObservationKind.Face) return Task.CompletedTask;
        if (obs.EventId is not long eventoId) return Task.CompletedTask;
        if (!obs.Match.IsKnown && !push.NotifyUnknown) return Task.CompletedTask;

        // Silencio por persona y cámara: nadie quiere cuarenta avisos de la misma cara.
        var clave = $"{obs.CameraId}|{(obs.Match.IsKnown ? obs.Match.Label : obs.FaceClusterId?.ToString() ?? "?")}";
        var ahora = DateTimeOffset.UtcNow;
        var silencio = TimeSpan.FromSeconds(Math.Max(10, push.CooldownSeconds));

        var repetido = false;
        _ultimos.AddOrUpdate(clave, ahora, (_, previo) =>
        {
            if (ahora - previo < silencio) { repetido = true; return previo; }
            return ahora;
        });
        if (repetido) return Task.CompletedTask;

        var titulo = obs.Match.IsKnown
            ? $"Rostro conocido: {obs.Match.Label}"
            : "Rostro sin identificar";

        var cuerpo = $"{obs.CameraName} · {obs.Timestamp.ToLocalTime():dd/MM HH:mm:ss}";
        var ficha = _push.PhotoToken(eventoId);
        var foto = $"/api/push/foto/{eventoId}?k={ficha}";

        _push.SendToAll(
            titulo, cuerpo,
            url: $"/Notificacion?evento={eventoId}",
            icono: foto,
            imagen: foto,
            tag: clave);

        return Task.CompletedTask;
    }

    public Task OnCameraStatusAsync(CameraStatus status, CancellationToken ct = default) => Task.CompletedTask;
}
