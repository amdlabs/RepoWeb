using IVZVision.Core.Configuration;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Objects;

/// <summary>
/// Clasificador opcional de marca/modelo de vehículo: cualquier red de clasificación
/// de imágenes exportada a ONNX (ResNet, ViT, ConvNeXt… entrenada en Stanford Cars,
/// CompCars o un dataset propio) con un fichero de etiquetas, una por línea en el
/// mismo orden que las clases del modelo.
/// </summary>
public sealed class VehicleModelClassifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputSize;
    private readonly string[] _labels;
    private readonly ILogger _logger;

    public VehicleModelClassifier(string modelPath, string labelsPath, ModelsConfig models, ILogger logger)
    {
        _logger = logger;

        if (!File.Exists(labelsPath))
            throw new FileNotFoundException(
                $"Falta el fichero de etiquetas «{labelsPath}» (una marca/modelo por línea).", labelsPath);

        _labels = File.ReadAllLines(labelsPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

        if (_labels.Length == 0)
            throw new InvalidOperationException("El fichero de etiquetas de modelos de vehículo está vacío.");

        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();

        var dims = _session.InputMetadata[_inputName].Dimensions;
        _inputSize = dims.Length == 4 && dims[2] > 0 ? dims[2] : 224;

        _logger.LogInformation("Clasificador de modelos de vehículo cargado ({Size}px, {Classes} clases) desde {Path}",
                               _inputSize, _labels.Length, modelPath);
    }

    /// <summary>Clasifica el recorte de un vehículo. Devuelve la etiqueta y su confianza softmax.</summary>
    public (string Label, float Confidence) Classify(Mat vehicleCropBgr)
    {
        using var input = ImageOps.Letterbox(vehicleCropBgr, _inputSize, _inputSize, out _);

        var tensor = OnnxSessionFactory.ToTensorImagenet(input);

        using var results = _session.Run(new[] { OnnxSessionFactory.CreateInput(_session, _inputName, tensor) });
        var logits = OnnxSessionFactory.ToFloatArray(results.First(), out _);

        // Softmax numéricamente estable sobre la salida.
        var max = logits.Max();
        var exp = logits.Select(v => MathF.Exp(v - max)).ToArray();
        var sum = exp.Sum();

        var best = 0;
        for (var i = 1; i < exp.Length; i++)
            if (exp[i] > exp[best]) best = i;

        var label = best < _labels.Length ? _labels[best] : $"clase {best}";
        return (label, exp[best] / sum);
    }

    public void Dispose() => _session.Dispose();
}
