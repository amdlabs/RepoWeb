using System.Diagnostics;
using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Data;
using IVZVision.Data.Entities;
using IVZVision.Vision.Drawing;
using IVZVision.Vision.Engine;
using IVZVision.Vision.Imaging;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace IVZVision.Vision.Pipeline;

/// <summary>
/// Procesa una cámara de principio a fin: abre el RTSP, reconecta si se cae,
/// analiza fotogramas al ritmo configurado, dibuja los cuadrantes, publica el
/// vídeo anotado y registra los reconocimientos en la base de datos.
/// </summary>
public sealed class CameraWorker
{
    private readonly CameraConfig _camera;
    private readonly IConfigStore _config;
    private readonly RecognitionEngine _engine;
    private readonly EventRecorder _recorder;
    private readonly FrameBroadcaster _broadcaster;
    private readonly IReadOnlyList<IObservationSink> _sinks;
    private readonly string _snapshotsRoot;
    private readonly ILogger _logger;

    private readonly Dictionary<string, PlateTrack> _plateTracks = new(StringComparer.Ordinal);
    private readonly List<Observation> _recent = new();
    private readonly object _recentGate = new();

    private IReadOnlyList<Observation> _overlay = Array.Empty<Observation>();

    /// <summary>1 mientras hay un análisis de esta cámara en curso (el vídeo nunca lo espera).</summary>
    private int _analysisBusy;

    public CameraWorker(CameraConfig camera, IConfigStore config, RecognitionEngine engine,
                        EventRecorder recorder, FrameBroadcaster broadcaster,
                        IEnumerable<IObservationSink> sinks, string snapshotsRoot, ILogger logger)
    {
        _camera = camera;
        _config = config;
        _engine = engine;
        _recorder = recorder;
        _broadcaster = broadcaster;
        _sinks = sinks.ToList();
        _snapshotsRoot = snapshotsRoot;
        _logger = logger;

        Status = new CameraStatus
        {
            CameraId = camera.Id,
            Name = camera.Name,
            Enabled = camera.Enabled,
            RtspUrlMasked = camera.BuildRtspUrl(maskCredentials: true),
            State = "Iniciando",
        };
    }

    public CameraStatus Status { get; }

    public Guid CameraId => _camera.Id;

    /// <summary>Últimas detecciones publicadas, para rellenar el panel al abrir la web.</summary>
    public IReadOnlyList<Observation> RecentObservations
    {
        get { lock (_recentGate) return _recent.ToList(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var backoffSeconds = 2;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ct).ConfigureAwait(false);
                backoffSeconds = 2; // la sesión terminó de forma limpia
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Status.Connected = false;
                Status.State = "Error";
                Status.LastError = ex.Message;
                await NotifyStatusAsync(ct).ConfigureAwait(false);
                _logger.LogError(ex, "Cámara {Name}: fallo en la sesión de captura", _camera.Name);
            }

            if (ct.IsCancellationRequested) break;

            Status.State = $"Reconectando en {backoffSeconds}s";
            await NotifyStatusAsync(ct).ConfigureAwait(false);
            PublishStatusFrame($"Reconectando en {backoffSeconds}s", Status.LastError);

            try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            backoffSeconds = Math.Min(backoffSeconds * 2, 30);
        }

        Status.Connected = false;
        Status.State = "Detenida";
        await NotifyStatusAsync(CancellationToken.None).ConfigureAwait(false);
        PublishStatusFrame("Detenida");
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        Status.State = "Conectando";
        Status.LastError = null;
        await NotifyStatusAsync(ct).ConfigureAwait(false);
        PublishStatusFrame("Conectando...");

        using var capture = OpenCapture();

        if (!capture.IsOpened())
            throw new IOException(_camera.IsUsb
                ? $"No se pudo abrir la cámara USB n.º {_camera.UsbDeviceIndex}. " +
                  "Compruebe que está conectada y que ninguna otra aplicación la esté usando."
                : $"No se pudo abrir el flujo RTSP {_camera.BuildRtspUrl(maskCredentials: true)}. " +
                  "Compruebe IP, puerto, usuario, contraseña y que el canal exista.");

        // Búfer mínimo: interesa el fotograma más reciente, no el histórico.
        capture.Set(VideoCaptureProperties.BufferSize, 1);

        Status.Connected = true;
        Status.State = "En directo";
        await NotifyStatusAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Cámara {Name}: conectada", _camera.Name);

        var frame = new Mat();
        var analysisClock = Stopwatch.StartNew();
        var streamClock = Stopwatch.StartNew();
        var fpsClock = Stopwatch.StartNew();
        var framesInWindow = 0;
        var consecutiveFailures = 0;
        var lastFrameAt = DateTimeOffset.Now;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // VideoCapture.Read bloquea, por eso el bucle vive en un hilo propio.
                var ok = capture.Read(frame);

                if (!ok || frame.Empty())
                {
                    consecutiveFailures++;

                    if (DateTimeOffset.Now - lastFrameAt > TimeSpan.FromSeconds(Math.Max(5, _camera.ReadTimeoutSeconds)))
                        throw new IOException("Sin fotogramas: se ha superado el tiempo de espera de lectura.");

                    if (consecutiveFailures > 200)
                        throw new IOException("Demasiados fotogramas inválidos consecutivos.");

                    await Task.Delay(20, ct).ConfigureAwait(false);
                    continue;
                }

                consecutiveFailures = 0;
                lastFrameAt = DateTimeOffset.Now;
                Status.LastFrameAt = lastFrameAt;
                Status.FrameWidth = frame.Width;
                Status.FrameHeight = frame.Height;

                framesInWindow++;
                if (fpsClock.Elapsed.TotalSeconds >= 2)
                {
                    Status.MeasuredFps = Math.Round(framesInWindow / fpsClock.Elapsed.TotalSeconds, 1);
                    framesInWindow = 0;
                    fpsClock.Restart();
                    await NotifyStatusAsync(ct).ConfigureAwait(false);
                }

                var rec = _config.Current.Recognition;

                // El análisis va SIEMPRE aparte del bucle de captura: la inferencia se
                // reparte entre todas las cámaras y puede tardar; si el vídeo esperara
                // su turno, el feed se congelaría (fotos en vez de vídeo). Si el motor
                // sigue ocupado cuando toca analizar, ese fotograma simplemente se salta.
                var analysisInterval = _camera.AnalysisFps > 0 ? 1000.0 / _camera.AnalysisFps : 0;
                if (analysisInterval > 0 && analysisClock.Elapsed.TotalMilliseconds >= analysisInterval
                    && Interlocked.CompareExchange(ref _analysisBusy, 1, 0) == 0)
                {
                    analysisClock.Restart();

                    var snapshot = frame.Clone();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AnalyzeFrameAsync(snapshot, rec, ct).ConfigureAwait(false);
                            Status.FramesProcessed++;
                        }
                        catch (OperationCanceledException) { /* parada de la cámara */ }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Análisis de fotograma fallido en {Name}", _camera.Name);
                        }
                        finally
                        {
                            snapshot.Dispose();
                            Interlocked.Exchange(ref _analysisBusy, 0);
                        }
                    }, CancellationToken.None);
                }

                var streamInterval = rec.StreamFps > 0 ? 1000.0 / rec.StreamFps : 0;
                if (streamInterval <= 0 || streamClock.Elapsed.TotalMilliseconds >= streamInterval)
                {
                    streamClock.Restart();
                    PublishFrame(frame, rec);
                }
            }
        }
        finally
        {
            frame.Dispose();
            Status.Connected = false;
        }
    }

    private void PublishFrame(Mat frame, RecognitionConfig rec)
    {
        using var canvas = frame.Clone();

        if (rec.DrawOverlay)
        {
            var overlay = _overlay;
            if (overlay.Count > 0) Annotator.Draw(canvas, overlay);

            DrawFps(canvas);
        }

        _broadcaster.Publish(_camera.Id, ImageOps.EncodeJpeg(canvas, rec.StreamJpegQuality));
    }

    /// <summary>Rótulo con los fps medidos tras el procesado, en la esquina superior izquierda.</summary>
    private void DrawFps(Mat canvas)
    {
        var text = $"{Status.MeasuredFps:0.#} fps";
        var scale = Math.Clamp(canvas.Width / 1400.0, 0.5, 1.0);
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, scale, 1, out var baseline);

        Cv2.Rectangle(canvas, new Rect(6, 6, size.Width + 12, size.Height + baseline + 8),
                      new Scalar(0, 0, 0), -1);
        Cv2.PutText(canvas, text, new Point(12, 10 + size.Height),
                    HersheyFonts.HersheySimplex, scale, new Scalar(80, 220, 100), 1, LineTypes.AntiAlias);
    }

    private async Task AnalyzeFrameAsync(Mat frame, RecognitionConfig rec, CancellationToken ct)
    {
        var roi = ResolveRoi(frame.Width, frame.Height);

        using var region = roi.HasValue ? new Mat(frame, roi.Value) : frame.Clone();
        using var analysis = ImageOps.ScaleForAnalysis(region, _camera.MaxAnalysisWidth, out var scale);

        var items = _engine.Analyze(analysis, _camera);

        var offsetX = roi?.X ?? 0;
        var offsetY = roi?.Y ?? 0;

        var observations = new List<Observation>(items.Count);

        foreach (var item in items)
        {
            // De coordenadas del recorte analizado a coordenadas del fotograma original.
            var box = new BoxF(
                item.Box.X / scale + offsetX,
                item.Box.Y / scale + offsetY,
                item.Box.Width / scale,
                item.Box.Height / scale).ClampTo(frame.Width, frame.Height);

            observations.Add(new Observation
            {
                Kind = item.Kind,
                CameraId = _camera.Id,
                CameraName = _camera.Name,
                Box = box,
                DetectionScore = item.Score,
                PlateText = item.PlateText,
                OcrConfidence = item.OcrConfidence,
                ObjectClass = item.ObjectClass,
                Annotation = item.Annotation,
                Match = item.Match,
            });
        }

        AssociatePlatesToVehicles(observations);

        _overlay = observations;

        foreach (var obs in observations)
            await MaybeRegisterAsync(frame, obs, rec, ct).ConfigureAwait(false);
    }

    private static readonly HashSet<string> VehicleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "coche", "camion", "moto", "autobus", "bicicleta", "car", "truck", "motorcycle", "bus", "bicycle",
    };

    /// <summary>Marcas de vehículo reconocibles en los textos leídos de la escena.</summary>
    private static readonly HashSet<string> VehicleBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOYOTA", "HONDA", "NISSAN", "FORD", "CHEVROLET", "VOLKSWAGEN", "RENAULT", "PEUGEOT",
        "CITROEN", "FIAT", "BMW", "MERCEDES", "AUDI", "SEAT", "SKODA", "KIA", "HYUNDAI",
        "MAZDA", "SUBARU", "SUZUKI", "MITSUBISHI", "JEEP", "DODGE", "VOLVO", "LEXUS",
        "PORSCHE", "MINI", "OPEL", "DACIA", "CHERY", "BYD", "GEELY", "HAVAL", "ISUZU",
        "IVECO", "SCANIA", "MAN", "DAF", "YAMAHA", "KAWASAKI", "KTM", "DUCATI", "HARLEY",
        "BAJAJ", "MOTOMEL", "ZANELLA", "TESLA", "RAM",
    };

    /// <summary>
    /// Compone la identificación de cada vehículo detectado a partir de todo lo que
    /// se ve en el fotograma: la matrícula que cae dentro (y, si está registrada, su
    /// marca/modelo de la base de datos), el modelo estimado por el clasificador
    /// opcional y la marca leída como texto sobre la carrocería.
    /// Resultado típico: «coche · ABC123 · Toyota Corolla» o «coche · NISSAN».
    /// </summary>
    private static void AssociatePlatesToVehicles(List<Observation> observations)
    {
        foreach (var vehicle in observations.Where(o => o.Kind == ObservationKind.Object
                                                        && o.ObjectClass is not null
                                                        && VehicleClasses.Contains(o.ObjectClass)))
        {
            var parts = new List<string>();

            // 1) Matrícula dentro del vehículo; si es conocida, sus notas traen «Marca Modelo».
            var plate = observations.FirstOrDefault(p =>
                p.Kind == ObservationKind.Plate
                && !string.IsNullOrEmpty(p.PlateText)
                && Contains(vehicle.Box, p.Box));

            if (plate is not null)
            {
                parts.Add(plate.PlateText!);
                if (plate.Match.IsKnown && !string.IsNullOrWhiteSpace(plate.Match.Notes))
                    parts.Add(plate.Match.Notes!);
            }

            // 2) Modelo estimado por el clasificador (viene del motor en Annotation).
            if (!string.IsNullOrEmpty(vehicle.Annotation)
                && !parts.Any(p => p.Contains(vehicle.Annotation!, StringComparison.OrdinalIgnoreCase)))
                parts.Add(vehicle.Annotation!);

            // 3) Marca leída como texto sobre el propio vehículo.
            var brandText = observations.FirstOrDefault(t =>
                t.Kind == ObservationKind.Text
                && Contains(vehicle.Box, t.Box)
                && VehicleBrands.Contains(NormalizeBrand(t.Match.Label)));

            if (brandText is not null)
            {
                var brand = NormalizeBrand(brandText.Match.Label).ToUpperInvariant();
                if (!parts.Any(p => p.Contains(brand, StringComparison.OrdinalIgnoreCase)))
                    parts.Add(brand);
            }

            if (parts.Count > 0)
                vehicle.Annotation = string.Join(" · ", parts);
        }
    }

    private static string NormalizeBrand(string text) =>
        new(text.Where(char.IsLetter).ToArray());

    private static bool Contains(BoxF outer, BoxF inner)
    {
        var cx = inner.X + inner.Width / 2;
        var cy = inner.Y + inner.Height / 2;
        return cx >= outer.X && cx <= outer.Right && cy >= outer.Y && cy <= outer.Bottom;
    }

    private async Task MaybeRegisterAsync(Mat frame, Observation obs, RecognitionConfig rec, CancellationToken ct)
    {
        if (obs.Kind == ObservationKind.Plate)
        {
            if (string.IsNullOrEmpty(obs.PlateText)) return;      // lectura descartada por el OCR
            if (!IsPlateConfirmed(obs.PlateText, rec)) return;    // aún no hay lecturas suficientes
        }

        if (!obs.Match.IsKnown && !rec.RegisterUnknown) return;
        if (_recorder.IsThrottled(obs)) return;

        AttachCrop(frame, obs, rec);

        await _recorder.RecordAsync(obs, RecognitionSource.Local, ct).ConfigureAwait(false);

        TrimRecent(obs, Math.Max(5, rec.RecentDetectionsBuffer));

        foreach (var sink in _sinks)
        {
            try { await sink.OnObservationAsync(obs, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Un receptor de eventos ha fallado"); }
        }
    }

    private void TrimRecent(Observation obs, int max)
    {
        lock (_recentGate)
        {
            _recent.Insert(0, obs);
            if (_recent.Count > max)
                _recent.RemoveRange(max, _recent.Count - max);
        }
    }

    /// <summary>Recorta el sujeto del fotograma para el panel de la web y, si procede, lo guarda en disco.</summary>
    private void AttachCrop(Mat frame, Observation obs, RecognitionConfig rec)
    {
        var margin = obs.Kind == ObservationKind.Face ? 0.25f : 0.10f;

        using var crop = ImageOps.SafeCrop(frame, obs.Box.Expand(margin, frame.Width, frame.Height));
        if (crop is null) return;

        try
        {
            using var thumb = Thumbnail(crop, obs.Kind == ObservationKind.Face ? 180 : 320);
            var jpeg = ImageOps.EncodeJpeg(thumb, 85);
            obs.CropJpegBase64 = Convert.ToBase64String(jpeg);

            if (rec.SaveSnapshots)
                obs.CropPath = SaveSnapshot(jpeg, obs);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo generar el recorte del sujeto");
        }
    }

    private static Mat Thumbnail(Mat src, int maxSide)
    {
        var longest = Math.Max(src.Width, src.Height);
        if (longest <= maxSide) return src.Clone();

        var factor = (double)maxSide / longest;
        var dst = new Mat();
        Cv2.Resize(src, dst,
            new Size(Math.Max(1, (int)(src.Width * factor)), Math.Max(1, (int)(src.Height * factor))),
            0, 0, InterpolationFlags.Area);
        return dst;
    }

    private string? SaveSnapshot(byte[] jpeg, Observation obs)
    {
        try
        {
            var now = obs.Timestamp;
            var relativeDir = Path.Combine(now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"),
                                           _camera.Id.ToString("N"));
            var absoluteDir = Path.Combine(_snapshotsRoot, relativeDir);
            Directory.CreateDirectory(absoluteDir);

            var fileName = $"{now:HHmmssfff}_{obs.Kind}_{obs.Id[..8]}.jpg";
            File.WriteAllBytes(Path.Combine(absoluteDir, fileName), jpeg);

            return Path.Combine(relativeDir, fileName).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo guardar el recorte en disco");
            return null;
        }
    }

    /// <summary>
    /// Exige varias lecturas coincidentes antes de dar una matrícula por buena.
    /// Un único fotograma con reflejos puede producir un texto plausible pero falso.
    /// </summary>
    private bool IsPlateConfirmed(string plate, RecognitionConfig rec)
    {
        var required = Math.Max(1, rec.PlateConfirmationHits);
        var window = TimeSpan.FromSeconds(Math.Max(1, rec.PlateConfirmationWindowSeconds));
        var now = DateTimeOffset.UtcNow;

        lock (_plateTracks)
        {
            foreach (var stale in _plateTracks.Where(kv => now - kv.Value.LastSeen > window).Select(kv => kv.Key).ToList())
                _plateTracks.Remove(stale);

            if (!_plateTracks.TryGetValue(plate, out var track))
            {
                _plateTracks[plate] = new PlateTrack { Hits = 1, LastSeen = now };
                return required <= 1;
            }

            track.Hits++;
            track.LastSeen = now;

            if (track.Hits < required) return false;

            // Confirmada: se reinicia el contador para el siguiente paso del vehículo.
            track.Hits = 0;
            return true;
        }
    }

    private Rect? ResolveRoi(int width, int height)
    {
        var x = Math.Clamp(_camera.RoiXPercent, 0, 100);
        var y = Math.Clamp(_camera.RoiYPercent, 0, 100);
        var w = Math.Clamp(_camera.RoiWidthPercent, 1, 100);
        var h = Math.Clamp(_camera.RoiHeightPercent, 1, 100);

        if (x == 0 && y == 0 && w >= 100 && h >= 100) return null;

        var rect = new Rect(
            (int)(width * x / 100.0),
            (int)(height * y / 100.0),
            (int)(width * w / 100.0),
            (int)(height * h / 100.0));

        rect.Width = Math.Min(rect.Width, width - rect.X);
        rect.Height = Math.Min(rect.Height, height - rect.Y);

        return rect.Width < 32 || rect.Height < 32 ? null : rect;
    }

    /// <summary>Abre la fuente de vídeo: dispositivo local para USB, RTSP vía FFmpeg para el resto.</summary>
    private VideoCapture OpenCapture()
    {
        if (_camera.IsUsb)
            return OpenUsbCapture(_camera.UsbDeviceIndex);

        return new VideoCapture(_camera.BuildRtspUrl(), VideoCaptureAPIs.FFMPEG);
    }

    /// <summary>
    /// Abre una webcam local. En Windows se usa Media Foundation (MSMF), el backend
    /// estable en Windows 10/11: DirectShow puede provocar violaciones de acceso
    /// nativas (que matan el proceso) al leer tras una apertura fallida. Si MSMF no
    /// abre el dispositivo se intenta DirectShow como reserva.
    /// </summary>
    public static VideoCapture OpenUsbCapture(int deviceIndex)
    {
        var index = Math.Max(0, deviceIndex);

        if (!OperatingSystem.IsWindows())
            return new VideoCapture(index, VideoCaptureAPIs.ANY);

        var capture = new VideoCapture(index, VideoCaptureAPIs.MSMF);
        if (capture.IsOpened()) return capture;

        capture.Dispose();
        return new VideoCapture(index, VideoCaptureAPIs.DSHOW);
    }

    /// <summary>
    /// Publica un fotograma sintético con el estado de la cámara para que el visor
    /// web nunca se quede en negro mientras no hay vídeo real.
    /// </summary>
    private void PublishStatusFrame(string headline, string? detail = null)
    {
        try
        {
            using var canvas = new Mat(new Size(640, 360), MatType.CV_8UC3, new Scalar(24, 24, 24));

            // PutText sólo dibuja ASCII: se eliminan tildes y caracteres especiales.
            var name = ToAscii(_camera.Name);
            Cv2.PutText(canvas, name, new Point(24, 60),
                        HersheyFonts.HersheySimplex, 0.9, new Scalar(210, 210, 210), 2, LineTypes.AntiAlias);
            Cv2.PutText(canvas, ToAscii(headline), new Point(24, 110),
                        HersheyFonts.HersheySimplex, 0.7, new Scalar(90, 170, 250), 2, LineTypes.AntiAlias);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                var text = ToAscii(detail);
                if (text.Length > 70) text = text[..70] + "...";
                Cv2.PutText(canvas, text, new Point(24, 150),
                            HersheyFonts.HersheySimplex, 0.45, new Scalar(140, 140, 140), 1, LineTypes.AntiAlias);
            }

            _broadcaster.Publish(_camera.Id, ImageOps.EncodeJpeg(canvas, 70));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo publicar el fotograma de estado");
        }
    }

    private static string ToAscii(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (ch < 128 && !char.IsControl(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private async Task NotifyStatusAsync(CancellationToken ct)
    {
        foreach (var sink in _sinks)
        {
            try { await sink.OnCameraStatusAsync(Status, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Un receptor de estado ha fallado"); }
        }
    }

    /// <summary>Registra un reconocimiento que viene de la propia cámara (ANPR por ISAPI).</summary>
    public async Task PublishCameraEventAsync(Observation obs, CancellationToken ct)
    {
        if (_recorder.IsThrottled(obs)) return;

        await _recorder.RecordAsync(obs, RecognitionSource.CameraEvent, ct).ConfigureAwait(false);

        TrimRecent(obs, Math.Max(5, _config.Current.Recognition.RecentDetectionsBuffer));

        foreach (var sink in _sinks)
        {
            try { await sink.OnObservationAsync(obs, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Un receptor de eventos ha fallado"); }
        }
    }

    private sealed class PlateTrack
    {
        public int Hits { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }
}
