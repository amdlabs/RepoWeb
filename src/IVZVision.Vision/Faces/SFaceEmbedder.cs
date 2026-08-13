using IVZVision.Core.Configuration;
using IVZVision.Core.Util;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Faces;

/// <summary>
/// Genera el vector de características de un rostro con SFace
/// (<c>face_recognition_sface_2021dec.onnx</c>). Devuelve 128 dimensiones ya
/// normalizadas para poder comparar con similitud coseno.
/// </summary>
public sealed class SFaceEmbedder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly ILogger _logger;

    public SFaceEmbedder(string modelPath, ModelsConfig models, ILogger logger)
    {
        _logger = logger;
        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();
        ModelId = Path.GetFileNameWithoutExtension(modelPath);

        _logger.LogInformation("Extractor de embeddings faciales cargado desde {Path}", modelPath);
    }

    /// <summary>Identificador del modelo, guardado junto a cada plantilla para detectar incompatibilidades.</summary>
    public string ModelId { get; }

    /// <summary>Calcula el embedding a partir del fotograma completo y los puntos del rostro.</summary>
    public float[] Embed(Mat frameBgr, (float X, float Y)[] landmarks)
    {
        using var aligned = FaceAligner.Align(frameBgr, landmarks);
        return EmbedAligned(aligned);
    }

    /// <summary>Calcula el embedding de un recorte ya alineado a 112x112.</summary>
    public float[] EmbedAligned(Mat alignedBgr)
    {
        using var resized = alignedBgr.Width == FaceAligner.OutputSize && alignedBgr.Height == FaceAligner.OutputSize
            ? alignedBgr.Clone()
            : Resize(alignedBgr);

        // SFace consume BGR sin normalizar, igual que blobFromImage con escala 1.
        var tensor = OnnxSessionFactory.ToTensor(resized, swapRb: false, scale: 1f, mean: 0f, std: 1f);

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var raw = results.First().AsEnumerable<float>().ToArray();

        return VectorMath.L2Normalize(raw);
    }

    private static Mat Resize(Mat src)
    {
        var dst = new Mat();
        Cv2.Resize(src, dst, new Size(FaceAligner.OutputSize, FaceAligner.OutputSize), 0, 0, InterpolationFlags.Linear);
        return dst;
    }

    public void Dispose() => _session.Dispose();
}
