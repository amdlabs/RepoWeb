using System.Collections.Concurrent;
using IVZVision.Core.Detection;

namespace IVZVision.Vision.Pipeline;

/// <summary>
/// Distribuye el último fotograma JPEG de cada cámara a todos los clientes MJPEG
/// conectados, sin acumular búferes: si un cliente va lento simplemente se salta fotogramas.
/// </summary>
public sealed class FrameBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();

    public void Publish(Guid cameraId, byte[] jpeg) => GetChannel(cameraId).Publish(jpeg);

    public byte[]? GetLatest(Guid cameraId) => GetChannel(cameraId).Latest;

    /// <summary>Espera al siguiente fotograma de la cámara indicada.</summary>
    public Task<byte[]> WaitForNextAsync(Guid cameraId, CancellationToken ct) => GetChannel(cameraId).WaitAsync(ct);

    public void Remove(Guid cameraId) => _channels.TryRemove(cameraId, out _);

    private Channel GetChannel(Guid cameraId) => _channels.GetOrAdd(cameraId, _ => new Channel());

    private sealed class Channel
    {
        private readonly object _gate = new();
        private TaskCompletionSource<byte[]> _next = NewSource();

        public byte[]? Latest { get; private set; }

        public void Publish(byte[] jpeg)
        {
            TaskCompletionSource<byte[]> toSignal;
            lock (_gate)
            {
                Latest = jpeg;
                toSignal = _next;
                _next = NewSource();
            }

            toSignal.TrySetResult(jpeg);
        }

        public Task<byte[]> WaitAsync(CancellationToken ct)
        {
            TaskCompletionSource<byte[]> source;
            lock (_gate) source = _next;

            return ct.CanBeCanceled ? source.Task.WaitAsync(ct) : source.Task;
        }

        private static TaskCompletionSource<byte[]> NewSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Receptor de los reconocimientos y del estado de las cámaras. La capa web lo
/// implementa para reenviarlos al navegador por SignalR.
/// </summary>
public interface IObservationSink
{
    Task OnObservationAsync(Observation observation, CancellationToken ct = default);

    Task OnCameraStatusAsync(CameraStatus status, CancellationToken ct = default);
}
