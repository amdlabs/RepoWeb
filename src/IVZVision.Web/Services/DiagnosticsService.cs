using System.Diagnostics;
using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Faces;
using IVZVision.Vision.Isapi;
using IVZVision.Vision.Plates;
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

    /// <summary>Abre el RTSP, captura un fotograma y lo devuelve como vista previa.</summary>
    public async Task<TestResult> TestRtspAsync(CameraConfig camera, CancellationToken ct)
    {
        var masked = camera.BuildRtspUrl(maskCredentials: true);

        return await Task.Run(() =>
        {
            try
            {
                // La prueba usa siempre TCP: es el transporte fiable para diagnosticar.
                Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS",
                    "rtsp_transport;tcp|stimeout;8000000");

                using var capture = new VideoCapture(camera.BuildRtspUrl(), VideoCaptureAPIs.FFMPEG);
                if (!capture.IsOpened())
                    return new TestResult(false,
                        $"No se pudo abrir {masked}. Revise IP/puerto, usuario y contraseña, " +
                        "y que el canal y el perfil existan en la cámara.");

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

                return new TestResult(false, "Se abrió el flujo pero no llegó ningún fotograma en 10 segundos.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prueba RTSP fallida para {Url}", masked);
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
            using var detector = new YoloPlateDetector(
                models.Resolve(models.PlateDetectorPath, _environment.ContentRootPath), models, _logger);
            using var ocr = new CtcPlateOcr(
                models.Resolve(models.PlateOcrPath, _environment.ContentRootPath),
                models.Resolve(models.PlateOcrCharsetPath, _environment.ContentRootPath), models, _logger);
        });

        lines.Add("");
        lines.Add($"Proveedor de ejecución: {models.ExecutionProvider}");
        lines.Add($"Carpeta de modelos: {models.Resolve(".", _environment.ContentRootPath)}");

        return new TestResult(faces || plates, string.Join('\n', lines));
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
