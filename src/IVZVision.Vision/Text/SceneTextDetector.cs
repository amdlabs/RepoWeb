using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Text;

/// <summary>
/// Localiza texto en la escena con un detector DBNet (PP-OCR det). Devuelve los
/// cuadros donde hay texto; la lectura la hace después el OCR CRNN existente.
/// El postprocesado usa contornos de OpenCV sobre el mapa de probabilidad.
/// </summary>
public sealed class SceneTextDetector : IDisposable
{
    private const int InputSize = 640; // múltiplo de 32, requisito de DBNet

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly ILogger _logger;

    public SceneTextDetector(string modelPath, ModelsConfig models, ILogger logger)
    {
        _logger = logger;
        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();

        _logger.LogInformation("Detector de texto cargado ({Size}px) desde {Path}", InputSize, modelPath);
    }

    public IReadOnlyList<BoxF> Detect(Mat frameBgr, float threshold, int maxBoxes)
    {
        using var input = ImageOps.Letterbox(frameBgr, InputSize, InputSize, out var transform);

        var tensor = OnnxSessionFactory.ToTensorImagenet(input);

        using var results = _session.Run(new[] { OnnxSessionFactory.CreateInput(_session, _inputName, tensor) });

        var data = OnnxSessionFactory.ToFloatArray(results.First(), out var shape);

        // Salida esperada [1, 1, H, W]: mapa de probabilidad de texto.
        var height = shape[^2];
        var width = shape[^1];

        // Binarizado del mapa a una imagen de 8 bits para extraer contornos.
        var bytes = new byte[height * width];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = data[i] >= threshold ? (byte)255 : (byte)0;

        using var binary = Mat.FromPixelData(height, width, MatType.CV_8UC1, bytes);

        // Una dilatación pequeña une caracteres de la misma palabra/línea.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 3));
        Cv2.Dilate(binary, binary, kernel);

        Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External,
                         ContourApproximationModes.ApproxSimple);

        var boxes = new List<(BoxF Box, float Area)>();

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (rect.Width < 10 || rect.Height < 6) continue;

            // DBNet encoge los cuadros al entrenar: se expanden para cubrir el texto completo.
            var dx = rect.Width * 0.12f + 4;
            var dy = rect.Height * 0.35f + 3;

            var box = transform.ToSource(rect.X - dx, rect.Y - dy,
                                         rect.Width + dx * 2, rect.Height + dy * 2)
                               .ClampTo(frameBgr.Width, frameBgr.Height);

            if (box.Width < 12 || box.Height < 8) continue;

            boxes.Add((box, box.Area));
        }

        // Los textos más grandes primero: suelen ser los relevantes (carteles, rótulos).
        return boxes.OrderByDescending(b => b.Area)
                    .Take(Math.Max(1, maxBoxes))
                    .Select(b => b.Box)
                    .ToList();
    }

    public void Dispose() => _session.Dispose();
}
