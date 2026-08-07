using IVZVision.Core.Configuration;
using IVZVision.Core.Util;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Objects;

/// <summary>
/// Extractor de características de objetos (opcional). Convierte el recorte de un
/// objeto en un vector comparable por similitud coseno, lo que permite dos cosas:
/// reconocer por su apariencia los objetos a los que se ha puesto nombre, y buscar
/// objetos parecidos a una imagen de ejemplo.
///
/// Sirve cualquier codificador de imagen ONNX que devuelva un vector: el codificador
/// visual de CLIP, un MobileNet o un ResNet sin la capa de clasificación.
/// </summary>
public sealed class ObjectEmbedder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputSize;
    private readonly float _mean;
    private readonly float _std;
    private readonly ILogger _logger;

    public ObjectEmbedder(string modelPath, ModelsConfig models, ILogger logger)
    {
        _logger = logger;
        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();

        var dimensions = _session.InputMetadata[_inputName].Dimensions;
        var modelSize = dimensions.Length == 4 ? dimensions[2] : -1;
        _inputSize = modelSize > 0 ? modelSize
                   : (models.ObjectEmbedderInputSize > 0 ? models.ObjectEmbedderInputSize : 224);

        _mean = models.ObjectEmbedderMean;
        _std = models.ObjectEmbedderStd <= 0 ? 1f : models.ObjectEmbedderStd;

        ModelId = Path.GetFileNameWithoutExtension(modelPath);

        _logger.LogInformation("Extractor de características de objetos cargado ({Size}px) desde {Path}",
            _inputSize, modelPath);
    }

    public string ModelId { get; }

    public float[] Embed(Mat cropBgr)
    {
        using var resized = new Mat();
        Cv2.Resize(cropBgr, resized, new Size(_inputSize, _inputSize), 0, 0, InterpolationFlags.Linear);

        var tensor = OnnxSessionFactory.ToTensor(resized, swapRb: true, scale: 1f / 255f, mean: _mean, std: _std);

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var raw = results.First().AsEnumerable<float>().ToArray();

        return VectorMath.L2Normalize(raw);
    }

    public void Dispose() => _session.Dispose();
}
