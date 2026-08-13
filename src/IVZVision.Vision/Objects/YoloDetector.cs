using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Objects;

/// <summary>Detección genérica de un modelo YOLO: cuadro, confianza y clase.</summary>
public sealed record YoloDetection(BoxF Box, float Score, int ClassId, string ClassName);

/// <summary>
/// Detector YOLO exportado a ONNX. Admite el formato de salida de YOLOv5/v7
/// ([1, N, 5+clases], con objectness) y el de YOLOv8/v11 ([1, 4+clases, N], sin
/// objectness), detectándolo por la forma del tensor. Se usa tanto para matrículas
/// (una clase) como para objetos COCO (ochenta clases).
/// </summary>
public sealed class YoloDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputSize;
    private readonly string[] _classNames;
    private readonly ILogger _logger;

    public YoloDetector(string modelPath, int configuredInputSize, string? classNamesPath,
                        ModelsConfig models, ILogger logger)
    {
        _logger = logger;
        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();

        // Si el modelo exporta un tamaño de entrada fijo se respeta; si es dinámico
        // se usa el configurado.
        var dimensions = _session.InputMetadata[_inputName].Dimensions;
        var modelSize = dimensions.Length == 4 ? dimensions[2] : -1;
        _inputSize = modelSize > 0 ? modelSize : (configuredInputSize > 0 ? configuredInputSize : 640);

        _classNames = LoadClassNames(classNamesPath);

        _logger.LogInformation("Detector YOLO cargado ({Size}px, {Classes} clases) desde {Path}",
            _inputSize, _classNames.Length, modelPath);
    }

    public IReadOnlyList<string> ClassNames => _classNames;

    public IReadOnlyList<YoloDetection> Detect(Mat frameBgr, float scoreThreshold, float nmsThreshold)
    {
        using var input = ImageOps.Letterbox(frameBgr, _inputSize, _inputSize, out var transform);

        // YOLO espera RGB en el rango 0-1.
        var tensor = OnnxSessionFactory.ToTensor(input, swapRb: true, scale: 1f / 255f, mean: 0f, std: 1f);

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });

        var output = results.First();
        var data = output.AsEnumerable<float>().ToArray();
        var shape = output.AsTensor<float>().Dimensions.ToArray();

        var raw = shape.Length switch
        {
            3 => DecodeThreeDim(data, shape, scoreThreshold, transform),
            2 => DecodeRowMajor(data, shape[0], shape[1], hasObjectness: shape[1] >= 6, scoreThreshold, transform),
            _ => throw new InvalidOperationException(
                    $"Forma de salida no soportada [{string.Join(",", shape)}]. " +
                    "Se espera un modelo YOLO estándar exportado a ONNX."),
        };

        if (raw.Count == 0) return Array.Empty<YoloDetection>();

        // La supresión de no-máximos se aplica por clase: un coche y una persona
        // solapados son dos detecciones válidas, no una duplicada.
        var kept = new List<YoloDetection>();
        foreach (var group in raw.GroupBy(d => d.ClassId))
        {
            var items = group.ToList();
            var boxes = items.Select(i => i.Box).ToList();
            var scores = items.Select(i => i.Score).ToList();

            foreach (var index in ImageOps.NonMaxSuppression(boxes, scores, nmsThreshold))
                kept.Add(items[index]);
        }

        return kept.OrderByDescending(d => d.Score).ToList();
    }

    private List<YoloDetection> DecodeThreeDim(float[] data, int[] shape, float threshold, LetterboxTransform transform)
    {
        // [1, A, B]: si A > B la disposición es por filas (v5), si no es por canales (v8).
        var a = shape[1];
        var b = shape[2];

        return a > b
            ? DecodeRowMajor(data, rows: a, stride: b, hasObjectness: true, threshold, transform)
            : DecodeChannelMajor(data, channels: a, anchors: b, threshold, transform);
    }

    /// <summary>Formato YOLOv5: cada fila es [cx, cy, w, h, objectness, clase0..N].</summary>
    private List<YoloDetection> DecodeRowMajor(float[] data, int rows, int stride, bool hasObjectness,
                                               float threshold, LetterboxTransform transform)
    {
        var found = new List<YoloDetection>();
        if (stride < 5) return found;

        var classOffset = hasObjectness ? 5 : 4;

        for (var i = 0; i < rows; i++)
        {
            var o = i * stride;
            if (o + stride > data.Length) break;

            var objectness = hasObjectness ? data[o + 4] : 1f;
            if (objectness < threshold * 0.5f) continue; // descarte rápido

            var bestClass = 0f;
            var bestId = 0;
            for (var c = classOffset; c < stride; c++)
            {
                if (data[o + c] > bestClass) { bestClass = data[o + c]; bestId = c - classOffset; }
            }

            // Con una sola clase y sin canal de clases, la objectness es la puntuación.
            if (stride == classOffset) bestClass = 1f;

            var score = objectness * bestClass;
            if (score < threshold) continue;

            Add(found, data[o], data[o + 1], data[o + 2], data[o + 3], score, bestId, transform);
        }

        return found;
    }

    /// <summary>Formato YOLOv8: [1, 4+clases, anclas], sin objectness.</summary>
    private List<YoloDetection> DecodeChannelMajor(float[] data, int channels, int anchors,
                                                   float threshold, LetterboxTransform transform)
    {
        var found = new List<YoloDetection>();
        if (channels < 5) return found;

        for (var i = 0; i < anchors; i++)
        {
            var best = 0f;
            var bestId = 0;
            for (var c = 4; c < channels; c++)
            {
                var v = data[c * anchors + i];
                if (v > best) { best = v; bestId = c - 4; }
            }

            if (best < threshold) continue;

            Add(found,
                data[0 * anchors + i], data[1 * anchors + i],
                data[2 * anchors + i], data[3 * anchors + i],
                best, bestId, transform);
        }

        return found;
    }

    private void Add(List<YoloDetection> found, float cx, float cy, float w, float h,
                     float score, int classId, LetterboxTransform transform)
    {
        if (w <= 1 || h <= 1) return;

        var box = transform.ToSource(cx - w / 2f, cy - h / 2f, w, h);
        if (box.Width < 4 || box.Height < 4) return;

        found.Add(new YoloDetection(box, score, classId, NameOf(classId)));
    }

    private string NameOf(int classId) =>
        classId >= 0 && classId < _classNames.Length ? _classNames[classId] : $"clase_{classId}";

    private static string[] LoadClassNames(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new[] { "objeto" };   // detector de una sola clase (matrículas)

        return File.ReadAllLines(path)
                   .Select(l => l.Trim())
                   .Where(l => l.Length > 0 && !l.StartsWith('#'))
                   .ToArray();
    }

    public void Dispose() => _session.Dispose();
}
