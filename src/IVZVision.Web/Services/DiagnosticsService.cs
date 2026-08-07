using System.Diagnostics;
using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Vision.Capture;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Faces;
using IVZVision.Vision.Isapi;
using IVZVision.Vision.Objects;
using IVZVision.Vision.Text;
using OpenCvSharp;

namespace IVZVision.Web.Services;

public sealed record TestResult(bool Success, string Message, string? Preview = null, object? Details = null);

/// <summary>Pruebas de conexión que se lanzan desde la pantalla de configuración.</summary>
public sealed class DiagnosticsService
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DiagnosticsService> _logger;

    public DiagnosticsService(IWebHostEnvironment environment, ILogger<DiagnosticsService> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public Task<DatabaseCheckResult> TestDatabaseAsync(DatabaseConfig db, CancellationToken ct)
        => DatabaseProvisioner.TestAsync(db, ct);

    /// <summary>Abre la cámara (RTSP o USB), captura un fotograma y lo devuelve como vista previa.</summary>
    public async Task<TestResult> TestRtspAsync(CameraConfig camera, CancellationToken ct)
    {
        var source = camera.DescribeSource();

        return await Task.Run(() =>
        {
            try
            {
                // Para la prueba se fuerza TCP: es el transporte fiable para diagnosticar.
                if (camera.Source == CameraSource.Ip)
                {
                    Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS",
                        "rtsp_transport;tcp|stimeout;8000000");
                }

                using var capture = CameraSourceFactory.Open(camera, _logger);
                if (!capture.IsOpened())
                    return new TestResult(false, CameraSourceFactory.DescribeOpenFailure(camera));

                using var frame = new Mat();
                var stopwatch = Stopwatch.StartNew();

                while (stopwatch.Elapsed < TimeSpan.FromSeconds(10) && !ct.IsCancellationRequested)
                {
                    if (capture.Read(frame) && !frame.Empty())
                    {
                        using var thumb = Resize(frame, 640);
                        var jpeg = ImageOps.EncodeJpeg(thumb, 80);

                        return new TestResult(true,
                            $"Conectado. Resolución {frame.Width}×{frame.Height}.",
                            $"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}");
                    }
                }

                return new TestResult(false, "Se abrió el origen pero no llegó ningún fotograma en 10 segundos.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prueba de captura fallida para {Source}", source);
                return new TestResult(false, $"Error al conectar: {ex.Message}");
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<TestResult> TestIsapiAsync(CameraConfig camera, CancellationToken ct)
    {
        using var client = new HikvisionIsapiClient(camera, _logger);
        var info = await client.GetDeviceInfoAsync(ct).ConfigureAwait(false);

        return new TestResult(info.Success, info.Message, null, new
        {
            modelo = info.Model,
            serie = info.SerialNumber,
            firmware = info.Firmware,
        });
    }

    /// <summary>
    /// Intenta abrir de verdad cada modelo con los valores del formulario (todavía sin
    /// guardar) y los cierra a continuación, así que no interfiere con el motor en marcha.
    /// </summary>
    public TestResult CheckModels(ModelsConfig models)
    {
        var lines = new List<string>();
        var faces = TryLoad(lines, "Reconocimiento facial", () =>
        {
            using var detector = new YuNetFaceDetector(
                models.Resolve(models.FaceDetectorPath, _environment.ContentRootPath), models, _logger);
            using var embedder = new SFaceEmbedder(
                models.Resolve(models.FaceEmbedderPath, _environment.ContentRootPath), models, _logger);
        });

        var plates = TryLoad(lines, "Lectura de matrículas", () =>
        {
            using var detector = new YoloDetector(
                models.Resolve(models.PlateDetectorPath, _environment.ContentRootPath),
                models.PlateDetectorInputSize, classNamesPath: null, models, _logger);
            using var ocr = new CtcTextRecognizer(
                models.Resolve(models.PlateOcrPath, _environment.ContentRootPath),
                models.Resolve(models.PlateOcrCharsetPath, _environment.ContentRootPath),
                CtcOptions.ForPlates(models), models, _logger);
        });

        var objects = TryLoad(lines, "Detección de objetos", () =>
        {
            using var detector = new YoloDetector(
                models.Resolve(models.ObjectDetectorPath, _environment.ContentRootPath),
                models.ObjectDetectorInputSize,
                models.Resolve(models.ObjectClassesPath, _environment.ContentRootPath), models, _logger);
            lines.Add($"       {detector.ClassNames.Count} clases disponibles.");
        });

        if (!string.IsNullOrWhiteSpace(models.ObjectEmbedderPath))
        {
            TryLoad(lines, "Características de objetos", () =>
            {
                using var embedder = new ObjectEmbedder(
                    models.Resolve(models.ObjectEmbedderPath, _environment.ContentRootPath), models, _logger);
            });
        }
        else
        {
            lines.Add("--  Características de objetos: sin configurar (los objetos no se reconocerán por su apariencia).");
        }

        var text = TryLoad(lines, "Lectura de texto", () =>
        {
            using var detector = new DbTextDetector(
                models.Resolve(models.TextDetectorPath, _environment.ContentRootPath), models, _logger);
            using var recognizer = new CtcTextRecognizer(
                models.Resolve(models.TextRecognizerPath, _environment.ContentRootPath),
                models.Resolve(models.TextCharsetPath, _environment.ContentRootPath),
                CtcOptions.ForText(models), models, _logger);
        });

        lines.Add("OK · Códigos QR y de barras: siempre disponibles (no necesitan modelo).");
        lines.Add("");
        lines.Add($"Proveedor de ejecución: {models.ExecutionProvider}");
        lines.Add($"Carpeta de modelos: {models.Resolve(".", _environment.ContentRootPath)}");

        return new TestResult(faces || plates || objects || text, string.Join('\n', lines));
    }

    private bool TryLoad(List<string> lines, string label, Action load)
    {
        try
        {
            load();
            lines.Add($"OK · {label} operativo.");
            return true;
        }
        catch (Exception ex)
        {
            lines.Add($"ERROR · {label}: {ex.Message}");
            return false;
        }
    }

    private static Mat Resize(Mat src, int maxWidth)
    {
        if (src.Width <= maxWidth) return src.Clone();

        var factor = (double)maxWidth / src.Width;
        var dst = new Mat();
        Cv2.Resize(src, dst, new Size(maxWidth, Math.Max(1, (int)(src.Height * factor))),
                   0, 0, InterpolationFlags.Area);
        return dst;
    }
}
