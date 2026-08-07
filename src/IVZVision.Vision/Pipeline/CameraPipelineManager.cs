using System.Collections.Concurrent;
using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Core.Util;
using IVZVision.Data;
using IVZVision.Vision.Engine;
using IVZVision.Vision.Isapi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IVZVision.Vision.Pipeline;

/// <summary>
/// Arranca y para un <see cref="CameraWorker"/> por cámara habilitada, mantiene el
/// índice de sujetos conocidos al día y purga el histórico. Al guardar la
/// configuración desde la web se reinicia todo automáticamente.
/// </summary>
public sealed class CameraPipelineManager : IHostedService, IDisposable
{
    private readonly IConfigStore _config;
    private readonly RecognitionEngine _engine;
    private readonly EventRecorder _recorder;
    private readonly KnownSubjectsIndex _index;
    private readonly DatabaseProvisioner _provisioner;
    private readonly FrameBroadcaster _broadcaster;
    private readonly IEnumerable<IObservationSink> _sinks;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CameraPipelineManager> _logger;
    private readonly string _contentRoot;

    private readonly ConcurrentDictionary<Guid, Running> _workers = new();
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    private CancellationTokenSource? _lifetime;
    private Task? _maintenance;
    private DateTime _lastPurgeDate = DateTime.MinValue;

    public CameraPipelineManager(IConfigStore config, RecognitionEngine engine, EventRecorder recorder,
                                 KnownSubjectsIndex index, DatabaseProvisioner provisioner,
                                 FrameBroadcaster broadcaster, IEnumerable<IObservationSink> sinks,
                                 ILoggerFactory loggerFactory, string contentRoot)
    {
        _config = config;
        _engine = engine;
        _recorder = recorder;
        _index = index;
        _provisioner = provisioner;
        _broadcaster = broadcaster;
        _sinks = sinks;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CameraPipelineManager>();
        _contentRoot = contentRoot;

        _config.Changed += OnConfigChanged;
    }

    public IReadOnlyList<CameraStatus> Statuses =>
        _workers.Values.Select(w => w.Worker.Status).OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();

    public CameraStatus? GetStatus(Guid cameraId) =>
        _workers.TryGetValue(cameraId, out var running) ? running.Worker.Status : null;

    public IReadOnlyList<Observation> GetRecentObservations(Guid? cameraId = null, int take = 40)
    {
        var source = cameraId.HasValue
            ? (_workers.TryGetValue(cameraId.Value, out var w) ? w.Worker.RecentObservations : Array.Empty<Observation>())
            : _workers.Values.SelectMany(x => x.Worker.RecentObservations).ToList();

        return source.OrderByDescending(o => o.Timestamp).Take(take).ToList();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = new CancellationTokenSource();

        // El arranque no debe bloquear el host: si SQL o la cámara no responden,
        // la web tiene que levantar igualmente para poder corregir la configuración.
        _maintenance = Task.Run(() => MaintenanceLoopAsync(_lifetime.Token), CancellationToken.None);

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _config.Changed -= OnConfigChanged;

        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);

        await StopWorkersAsync().ConfigureAwait(false);

        if (_maintenance is not null)
        {
            try { await _maintenance.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
            catch (Exception) { /* el host se está cerrando */ }
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        await _provisioner.EnsureReadyAsync(ct).ConfigureAwait(false);
        await _index.RefreshAsync(ct).ConfigureAwait(false);
        await RestartWorkersAsync(ct).ConfigureAwait(false);

        var lastIndexRefresh = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

                if (_index.IsDirty || DateTimeOffset.UtcNow - lastIndexRefresh > KnownSubjectsIndex.RefreshInterval)
                {
                    await _index.RefreshAsync(ct).ConfigureAwait(false);
                    lastIndexRefresh = DateTimeOffset.UtcNow;
                }

                await MaybePurgeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en el ciclo de mantenimiento");
            }
        }
    }

    private async Task MaybePurgeAsync(CancellationToken ct)
    {
        var storage = _config.Current.Storage;
        if (storage.RetentionDays <= 0) return;

        var now = DateTime.Now;
        if (now.Hour != Math.Clamp(storage.PurgeHour, 0, 23)) return;
        if (_lastPurgeDate == now.Date) return;

        _lastPurgeDate = now.Date;
        await _recorder.PurgeAsync(storage.Resolve(_contentRoot), ct).ConfigureAwait(false);
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        var token = _lifetime?.Token ?? CancellationToken.None;

        // El guardado ocurre en el hilo de la petición web: el reinicio va aparte.
        _ = Task.Run(async () =>
        {
            try
            {
                await _provisioner.EnsureReadyAsync(token).ConfigureAwait(false);
                await _index.RefreshAsync(token).ConfigureAwait(false);
                await RestartWorkersAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo aplicar la nueva configuración");
            }
        }, CancellationToken.None);
    }

    private async Task RestartWorkersAsync(CancellationToken ct)
    {
        await _restartLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopWorkersAsync().ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            var snapshotsRoot = _config.Current.Storage.Resolve(_contentRoot);
            Directory.CreateDirectory(snapshotsRoot);

            ApplyFfmpegOptions();

            foreach (var camera in _config.Current.Cameras.Where(c => c.Enabled))
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                var worker = new CameraWorker(camera, _config, _engine, _recorder, _broadcaster, _sinks,
                                              snapshotsRoot, _loggerFactory.CreateLogger<CameraWorker>());

                // Hilo dedicado: VideoCapture.Read es una llamada nativa bloqueante.
                var task = Task.Factory.StartNew(
                    () => worker.RunAsync(cts.Token),
                    cts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();

                Task? isapi = null;
                if (camera.UseCameraAnprEvents && !camera.IsUsb)
                    isapi = Task.Run(() => ListenCameraAnprAsync(worker, camera, cts.Token), CancellationToken.None);

                _workers[camera.Id] = new Running(worker, task, isapi, cts);

                _logger.LogInformation("Cámara {Name} arrancada ({Url})", camera.Name,
                                       camera.BuildRtspUrl(maskCredentials: true));
            }

            if (_workers.IsEmpty)
                _logger.LogWarning("No hay ninguna cámara habilitada; añada una en la pantalla de configuración.");
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>
    /// OpenCV configura FFmpeg mediante una variable de entorno que se lee al abrir
    /// cada captura, por eso se fija aquí, justo antes de arrancar los hilos.
    /// El transporte TCP evita los fotogramas rotos típicos de RTSP sobre UDP y el
    /// tiempo de espera impide que una cámara caída bloquee el hilo indefinidamente.
    /// </summary>
    private void ApplyFfmpegOptions()
    {
        // Las cámaras USB no pasan por FFmpeg: sólo cuentan las de red.
        var network = _config.Current.Cameras.Where(c => c.Enabled && !c.IsUsb).ToList();
        if (network.Count == 0) return;

        var useTcp = network.Any(c => c.UseTcpTransport);
        var timeoutMicroseconds = Math.Max(5, network
            .Select(c => c.ReadTimeoutSeconds)
            .DefaultIfEmpty(15)
            .Max()) * 1_000_000L;

        // "stimeout" es la opción clásica; FFmpeg 5+ la renombró a "timeout".
        // Se envían las dos: la que no exista en la versión enlazada se ignora.
        var options = useTcp
            ? $"rtsp_transport;tcp|stimeout;{timeoutMicroseconds}|timeout;{timeoutMicroseconds}"
            : $"stimeout;{timeoutMicroseconds}|timeout;{timeoutMicroseconds}";

        Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", options);
        _logger.LogInformation("Opciones FFmpeg para RTSP: {Options}", options);
    }

    private async Task StopWorkersAsync()
    {
        var running = _workers.Values.ToList();
        _workers.Clear();

        foreach (var item in running)
        {
            try { await item.Cts.CancelAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error al cancelar una cámara"); }
        }

        foreach (var item in running)
        {
            try
            {
                var tasks = new List<Task> { item.Task };
                if (item.Isapi is not null) tasks.Add(item.Isapi);
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // El hilo de captura puede estar bloqueado dentro de FFmpeg; se abandona.
            }
            finally
            {
                item.Cts.Dispose();
                _broadcaster.Remove(item.Worker.CameraId);
            }
        }
    }

    /// <summary>Escucha los eventos ANPR que emite la propia cámara y los registra como reconocimientos.</summary>
    private async Task ListenCameraAnprAsync(CameraWorker worker, CameraConfig camera, CancellationToken ct)
    {
        var logger = _loggerFactory.CreateLogger<HikvisionIsapiClient>();
        var backoff = 5;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new HikvisionIsapiClient(camera, logger);

                await foreach (var evt in client.StreamPlateEventsAsync(ct).ConfigureAwait(false))
                {
                    var normalized = PlateText.Normalize(evt.Plate);
                    if (normalized.Length == 0) continue;

                    var match = _index.MatchPlate(normalized);

                    await worker.PublishCameraEventAsync(new Observation
                    {
                        Kind = ObservationKind.Plate,
                        CameraId = camera.Id,
                        CameraName = camera.Name,
                        Timestamp = evt.Timestamp,
                        PlateText = normalized,
                        OcrConfidence = 1f,
                        DetectionScore = 1f,
                        Match = match,
                    }, ct).ConfigureAwait(false);

                    backoff = 5;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cámara {Name}: se perdió el flujo de eventos ISAPI", camera.Name);
            }

            if (ct.IsCancellationRequested) break;

            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            backoff = Math.Min(backoff * 2, 60);
        }
    }

    public void Dispose()
    {
        _lifetime?.Dispose();
        _restartLock.Dispose();
    }

    private sealed record Running(CameraWorker Worker, Task Task, Task? Isapi, CancellationTokenSource Cts);
}
