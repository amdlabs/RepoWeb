using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Objects;

public sealed record DetectedObject(BoxF Box, float Score, string ClassName);

/// <summary>
/// Detección genérica de objetos (personas, vehículos, animales…) con un modelo
/// COCO en formato YOLO exportado a ONNX. Admite las mismas variantes de salida
/// que el detector de matrículas: YOLOv5/v7 ([1, N, 5+clases]) y YOLOv8/v11
/// ([1, 4+clases, N]).
/// </summary>
public sealed class YoloObjectDetector : IDisposable
{
    /// <summary>Las 80 clases COCO en español, en el orden estándar del dataset.</summary>
    private static readonly string[] CocoEs =
    {
        "persona", "bicicleta", "coche", "moto", "avion", "autobus", "tren", "camion",
        "barco", "semaforo", "boca de incendios", "señal de stop", "parquimetro", "banco",
        "pajaro", "gato", "perro", "caballo", "oveja", "vaca", "elefante", "oso", "cebra",
        "jirafa", "mochila", "paraguas", "bolso", "corbata", "maleta", "frisbee", "esquis",
        "snowboard", "pelota", "cometa", "bate de beisbol", "guante de beisbol", "monopatin",
        "tabla de surf", "raqueta de tenis", "botella", "copa de vino", "taza", "tenedor",
        "cuchillo", "cuchara", "cuenco", "platano", "manzana", "sandwich", "naranja",
        "brocoli", "zanahoria", "perrito caliente", "pizza", "donut", "tarta", "silla",
        "sofa", "planta", "cama", "mesa", "inodoro", "televisor", "portatil", "raton",
        "mando a distancia", "teclado", "telefono movil", "microondas", "horno", "tostadora",
        "fregadero", "nevera", "libro", "reloj", "jarron", "tijeras", "oso de peluche",
        "secador", "cepillo de dientes",
    };

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputSize;
    private readonly string[] _labels;
    private readonly ILogger _logger;

    public YoloObjectDetector(string modelPath, string? labelsPath, ModelsConfig models, ILogger logger)
    {
        _logger = logger;
        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();

        var dimensions = _session.InputMetadata[_inputName].Dimensions;
        var modelSize = dimensions.Length == 4 ? dimensions[2] : -1;
        _inputSize = modelSize > 0
            ? modelSize
            : (models.ObjectDetectorInputSize > 0 ? models.ObjectDetectorInputSize : 640);

        _labels = LoadLabels(labelsPath);

        _logger.LogInformation("Detector de objetos cargado ({Size}px, {Classes} clases) desde {Path}",
                               _inputSize, _labels.Length, modelPath);
    }

    private string[] LoadLabels(string? labelsPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(labelsPath) && File.Exists(labelsPath))
            {
                var lines = File.ReadAllLines(labelsPath)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .ToArray();
                if (lines.Length > 0) return lines;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer el fichero de clases {Path}; se usan las clases COCO", labelsPath);
        }

        return CocoEs;
    }

    public string ClassNameOf(int classId) =>
        classId >= 0 && classId < _labels.Length ? _labels[classId] : $"clase {classId}";

    public IReadOnlyList<DetectedObject> Detect(Mat frameBgr, float scoreThreshold, float nmsThreshold)
    {
        using var input = ImageOps.Letterbox(frameBgr, _inputSize, _inputSize, out var transform);

        var tensor = OnnxSessionFactory.ToTensor(input, swapRb: true, scale: 1f / 255f, mean: 0f, std: 1f);

        using var results = _session.Run(new[] { OnnxSessionFactory.CreateInput(_session, _inputName, tensor) });

        var data = OnnxSessionFactory.ToFloatArray(results.First(), out var shape);

        var (boxes, scores, classes) = shape.Length switch
        {
            3 => DecodeThreeDim(data, shape, scoreThreshold, transform),
            2 => DecodeRowMajor(data, shape[0], shape[1], hasObjectness: shape[1] >= 6, scoreThreshold, transform),
            _ => throw new InvalidOperationException(
                    $"Forma de salida no soportada [{string.Join(",", shape)}]. " +
                    "Se espera un modelo YOLO COCO estándar exportado a ONNX."),
        };

        if (boxes.Count == 0) return Array.Empty<DetectedObject>();

        // La supresión de no-máximos se hace por clase: un perro y una persona
        // pueden solaparse sin que uno elimine al otro.
        var result = new List<DetectedObject>();
        foreach (var group in Enumerable.Range(0, boxes.Count).GroupBy(i => classes[i]))
        {
            var idx = group.ToList();
            var groupBoxes = idx.Select(i => boxes[i]).ToList();
            var groupScores = idx.Select(i => scores[i]).ToList();

            var keep = ImageOps.NonMaxSuppression(groupBoxes, groupScores, nmsThreshold);
            result.AddRange(keep.Select(k =>
                new DetectedObject(groupBoxes[k], groupScores[k], ClassNameOf(group.Key))));
        }

        return result;
    }

    private (List<BoxF>, List<float>, List<int>) DecodeThreeDim(
        float[] data, int[] shape, float threshold, LetterboxTransform transform)
    {
        var a = shape[1];
        var b = shape[2];

        return a > b
            ? DecodeRowMajor(data, rows: a, stride: b, hasObjectness: true, threshold, transform)
            : DecodeChannelMajor(data, channels: a, anchors: b, threshold, transform);
    }

    /// <summary>Formato YOLOv5: cada fila es [cx, cy, w, h, objectness, clase0..N].</summary>
    private (List<BoxF>, List<float>, List<int>) DecodeRowMajor(
        float[] data, int rows, int stride, bool hasObjectness, float threshold, LetterboxTransform transform)
    {
        var boxes = new List<BoxF>();
        var scores = new List<float>();
        var classes = new List<int>();

        if (stride < 6) return (boxes, scores, classes);

        var classOffset = hasObjectness ? 5 : 4;

        for (var i = 0; i < rows; i++)
        {
            var o = i * stride;
            var objectness = hasObjectness ? data[o + 4] : 1f;
            if (objectness < threshold * 0.5f) continue;

            var bestClass = 0;
            var bestScore = 0f;
            for (var c = classOffset; c < stride; c++)
            {
                if (data[o + c] > bestScore)
                {
                    bestScore = data[o + c];
                    bestClass = c - classOffset;
                }
            }

            var score = objectness * bestScore;
            if (score < threshold) continue;

            Add(boxes, scores, classes, data[o], data[o + 1], data[o + 2], data[o + 3],
                score, bestClass, transform);
        }

        return (boxes, scores, classes);
    }

    /// <summary>Formato YOLOv8: [1, 4+clases, anclas], sin objectness.</summary>
    private (List<BoxF>, List<float>, List<int>) DecodeChannelMajor(
        float[] data, int channels, int anchors, float threshold, LetterboxTransform transform)
    {
        var boxes = new List<BoxF>();
        var scores = new List<float>();
        var classes = new List<int>();

        if (channels < 5) return (boxes, scores, classes);

        for (var i = 0; i < anchors; i++)
        {
            var bestClass = 0;
            var bestScore = 0f;
            for (var c = 4; c < channels; c++)
            {
                var v = data[c * anchors + i];
                if (v > bestScore)
                {
                    bestScore = v;
                    bestClass = c - 4;
                }
            }

            if (bestScore < threshold) continue;

            Add(boxes, scores, classes,
                data[0 * anchors + i], data[1 * anchors + i],
                data[2 * anchors + i], data[3 * anchors + i],
                bestScore, bestClass, transform);
        }

        return (boxes, scores, classes);
    }

    private static void Add(List<BoxF> boxes, List<float> scores, List<int> classes,
                            float cx, float cy, float w, float h, float score, int classId,
                            LetterboxTransform transform)
    {
        if (w <= 1 || h <= 1) return;
        var box = transform.ToSource(cx - w / 2f, cy - h / 2f, w, h);
        if (box.Width < 4 || box.Height < 4) return;

        boxes.Add(box);
        scores.Add(score);
        classes.Add(classId);
    }

    public void Dispose() => _session.Dispose();
}
