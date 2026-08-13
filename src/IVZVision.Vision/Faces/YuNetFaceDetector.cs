using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Faces;

/// <summary>Rostro localizado con sus cinco puntos de referencia (ojos, nariz y comisuras).</summary>
public sealed record DetectedFace(BoxF Box, float Score, (float X, float Y)[] Landmarks);

/// <summary>
/// Detector de rostros YuNet ejecutado localmente con ONNX Runtime.
/// Modelo: <c>face_detection_yunet_2023mar.onnx</c> del OpenCV Model Zoo.
/// </summary>
public sealed class YuNetFaceDetector : IDisposable
{
    private static readonly int[] Strides = { 8, 16, 32 };

    private readonly InferenceSession _session;
    private readonly ILogger _logger;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly string _inputName;

    public YuNetFaceDetector(string modelPath, ModelsConfig models, ILogger logger)
    {
        _logger = logger;
        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();

        // El YuNet publicado tiene la entrada fija en 640x640. Si el modelo declara un
        // tamaño concreto manda el modelo; sólo se usa la configuración cuando es dinámico.
        var dimensions = _session.InputMetadata[_inputName].Dimensions;
        var modelHeight = dimensions.Length == 4 ? dimensions[2] : -1;
        var modelWidth = dimensions.Length == 4 ? dimensions[3] : -1;

        // YuNet trabaja con rejillas de 8/16/32 px: el lado debe ser múltiplo de 32.
        _inputWidth = modelWidth > 0 ? modelWidth : RoundToMultiple(models.FaceDetectorInputWidth, 32);
        _inputHeight = modelHeight > 0 ? modelHeight : RoundToMultiple(models.FaceDetectorInputHeight, 32);

        _logger.LogInformation("Detector de rostros YuNet cargado ({Width}x{Height}{Source}) desde {Path}",
            _inputWidth, _inputHeight,
            modelWidth > 0 ? ", tamaño fijado por el modelo" : "", modelPath);
    }

    public IReadOnlyList<DetectedFace> Detect(Mat frameBgr, float scoreThreshold, float nmsThreshold, int minFaceSize)
    {
        using var input = ImageOps.Letterbox(frameBgr, _inputWidth, _inputHeight, out var transform);

        // YuNet espera BGR sin normalizar (valores 0-255).
        var tensor = OnnxSessionFactory.ToTensor(input, swapRb: false, scale: 1f, mean: 0f, std: 1f);

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });

        var outputs = results.ToDictionary(r => r.Name, r => r.AsEnumerable<float>().ToArray(), StringComparer.Ordinal);

        var boxes = new List<BoxF>();
        var scores = new List<float>();
        var landmarks = new List<(float X, float Y)[]>();

        foreach (var stride in Strides)
        {
            if (!outputs.TryGetValue($"cls_{stride}", out var cls) ||
                !outputs.TryGetValue($"obj_{stride}", out var obj) ||
                !outputs.TryGetValue($"bbox_{stride}", out var bbox) ||
                !outputs.TryGetValue($"kps_{stride}", out var kps))
            {
                throw new InvalidOperationException(
                    $"El modelo no expone las salidas cls_{stride}/obj_{stride}/bbox_{stride}/kps_{stride}. " +
                    "Compruebe que es el YuNet del OpenCV Model Zoo (face_detection_yunet_*.onnx). " +
                    $"Salidas encontradas: {string.Join(", ", outputs.Keys)}");
            }

            var cols = _inputWidth / stride;
            var rows = _inputHeight / stride;
            var cells = Math.Min(rows * cols, Math.Min(cls.Length, obj.Length));

            for (var i = 0; i < cells; i++)
            {
                var clsScore = Math.Clamp(cls[i], 0f, 1f);
                var objScore = Math.Clamp(obj[i], 0f, 1f);
                var score = MathF.Sqrt(clsScore * objScore);
                if (score < scoreThreshold) continue;

                var r = i / cols;
                var c = i % cols;

                var b = i * 4;
                var cx = (c + bbox[b + 0]) * stride;
                var cy = (r + bbox[b + 1]) * stride;
                var w = MathF.Exp(bbox[b + 2]) * stride;
                var h = MathF.Exp(bbox[b + 3]) * stride;

                var source = transform.ToSource(cx - w / 2f, cy - h / 2f, w, h);
                if (source.Width < minFaceSize || source.Height < minFaceSize) continue;

                var pts = new (float X, float Y)[5];
                var k = i * 10;
                for (var p = 0; p < 5; p++)
                {
                    var lx = (c + kps[k + 2 * p]) * stride;
                    var ly = (r + kps[k + 2 * p + 1]) * stride;
                    pts[p] = transform.PointToSource(lx, ly);
                }

                boxes.Add(source);
                scores.Add(score);
                landmarks.Add(pts);
            }
        }

        if (boxes.Count == 0) return Array.Empty<DetectedFace>();

        var keep = ImageOps.NonMaxSuppression(boxes, scores, nmsThreshold);
        return keep.Select(i => new DetectedFace(boxes[i], scores[i], landmarks[i])).ToList();
    }

    private static int RoundToMultiple(int value, int multiple)
    {
        if (value <= 0) return multiple * 20; // 640 por defecto
        var rounded = (int)Math.Round(value / (double)multiple) * multiple;
        return Math.Max(multiple, rounded);
    }

    public void Dispose() => _session.Dispose();
}
