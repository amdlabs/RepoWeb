using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Plates;

public sealed record DetectedPlate(BoxF Box, float Score);

/// <summary>
/// Localiza matrículas con un modelo YOLO exportado a ONNX. Admite el formato de
/// salida de YOLOv5/v7 ([1, N, 5+clases], con objectness) y el de YOLOv8/v11
/// ([1, 4+clases, N], sin objectness), detectándolo por la forma del tensor.
/// </summary>
public sealed class YoloPlateDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputSize;
    private readonly ILogger _logger;

    public YoloPlateDetector(string modelPath, ModelsConfig models, ILogger logger)
    {
        _logger = logger;
        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();

        // Si el modelo exporta un tamaño de entrada fijo se respeta; si es dinámico
        // se usa el configurado.
        var dimensions = _session.InputMetadata[_inputName].Dimensions;
        var modelSize = dimensions.Length == 4 ? dimensions[2] : -1;

        _inputSize = modelSize > 0
            ? modelSize
            : (models.PlateDetectorInputSize > 0 ? models.PlateDetectorInputSize : 640);

        _logger.LogInformation("Detector de matrículas cargado ({Size}px) desde {Path}", _inputSize, modelPath);
    }

    public IReadOnlyList<DetectedPlate> Detect(Mat frameBgr, float scoreThreshold, float nmsThreshold)
    {
        using var input = ImageOps.Letterbox(frameBgr, _inputSize, _inputSize, out var transform);

        // YOLO espera RGB en el rango 0-1.
        var tensor = OnnxSessionFactory.ToTensor(input, swapRb: true, scale: 1f / 255f, mean: 0f, std: 1f);

        using var results = _session.Run(new[] { OnnxSessionFactory.CreateInput(_session, _inputName, tensor) });

        var data = OnnxSessionFactory.ToFloatArray(results.First(), out var shape);

        var (boxes, scores) = shape.Length switch
        {
            3 => DecodeThreeDim(data, shape, scoreThreshold, transform),
            2 => DecodeTwoDim(data, shape, scoreThreshold, transform),
            _ => throw new InvalidOperationException(
                    $"Forma de salida no soportada [{string.Join(",", shape)}]. " +
                    "Se espera un modelo YOLO estándar exportado a ONNX."),
        };

        if (boxes.Count == 0) return Array.Empty<DetectedPlate>();

        var keep = ImageOps.NonMaxSuppression(boxes, scores, nmsThreshold);
        return keep.Select(i => new DetectedPlate(boxes[i], scores[i])).ToList();
    }

    private (List<BoxF> Boxes, List<float> Scores) DecodeThreeDim(
        float[] data, int[] shape, float threshold, LetterboxTransform transform)
    {
        // [1, A, B]: si A > B la disposición es por filas (v5), si no es por canales (v8).
        var a = shape[1];
        var b = shape[2];

        return a > b
            ? DecodeRowMajor(data, rows: a, stride: b, hasObjectness: true, threshold, transform)
            : DecodeChannelMajor(data, channels: a, anchors: b, threshold, transform);
    }

    private (List<BoxF>, List<float>) DecodeTwoDim(
        float[] data, int[] shape, float threshold, LetterboxTransform transform)
    {
        var rows = shape[0];
        var stride = shape[1];
        return DecodeRowMajor(data, rows, stride, hasObjectness: stride >= 6, threshold, transform);
    }

    /// <summary>Formato YOLOv5: cada fila es [cx, cy, w, h, objectness, clase0..N].</summary>
    private (List<BoxF>, List<float>) DecodeRowMajor(
        float[] data, int rows, int stride, bool hasObjectness, float threshold, LetterboxTransform transform)
    {
        var boxes = new List<BoxF>();
        var scores = new List<float>();

        if (stride < 5) return (boxes, scores);

        var classOffset = hasObjectness ? 5 : 4;

        for (var i = 0; i < rows; i++)
        {
            var o = i * stride;
            var objectness = hasObjectness ? data[o + 4] : 1f;
            if (objectness < threshold * 0.5f) continue; // descarte rápido

            var bestClass = 0f;
            for (var c = classOffset; c < stride; c++)
                if (data[o + c] > bestClass) bestClass = data[o + c];

            // Con una sola clase y sin canal de clases, la objectness es la puntuación.
            if (stride == classOffset) bestClass = 1f;

            var score = objectness * bestClass;
            if (score < threshold) continue;

            Add(boxes, scores, data[o], data[o + 1], data[o + 2], data[o + 3], score, transform);
        }

        return (boxes, scores);
    }

    /// <summary>Formato YOLOv8: [1, 4+clases, anclas], sin objectness.</summary>
    private (List<BoxF>, List<float>) DecodeChannelMajor(
        float[] data, int channels, int anchors, float threshold, LetterboxTransform transform)
    {
        var boxes = new List<BoxF>();
        var scores = new List<float>();

        if (channels < 5) return (boxes, scores);

        for (var i = 0; i < anchors; i++)
        {
            var best = 0f;
            for (var c = 4; c < channels; c++)
            {
                var v = data[c * anchors + i];
                if (v > best) best = v;
            }

            if (best < threshold) continue;

            Add(boxes, scores,
                data[0 * anchors + i], data[1 * anchors + i],
                data[2 * anchors + i], data[3 * anchors + i],
                best, transform);
        }

        return (boxes, scores);
    }

    private static void Add(List<BoxF> boxes, List<float> scores,
                            float cx, float cy, float w, float h, float score, LetterboxTransform transform)
    {
        if (w <= 1 || h <= 1) return;
        var box = transform.ToSource(cx - w / 2f, cy - h / 2f, w, h);
        if (box.Width < 4 || box.Height < 4) return;

        boxes.Add(box);
        scores.Add(score);
    }

    public void Dispose() => _session.Dispose();
}
