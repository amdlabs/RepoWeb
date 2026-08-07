using System.Text;
using IVZVision.Core.Configuration;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Plates;

public sealed record PlateReading(string Text, float Confidence);

/// <summary>
/// OCR de matrículas basado en un reconocedor de texto CRNN con decodificación CTC
/// (compatible con los modelos <c>rec</c> de PP-OCR y con CRNN genéricos).
/// El diccionario se carga de un fichero de texto con un carácter por línea.
/// </summary>
public sealed class CtcPlateOcr : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _charset;
    private readonly ModelsConfig _models;
    private readonly ILogger _logger;

    public CtcPlateOcr(string modelPath, string charsetPath, ModelsConfig models, ILogger logger)
    {
        _models = models;
        _logger = logger;
        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();
        _charset = LoadCharset(charsetPath);

        _logger.LogInformation("OCR de matrículas cargado desde {Path} ({Count} caracteres)", modelPath, _charset.Length);
    }

    public PlateReading Read(Mat plateBgr)
    {
        if (plateBgr.Width < 4 || plateBgr.Height < 4)
            return new PlateReading("", 0);

        using var prepared = Preprocess(plateBgr);

        var tensor = _models.PlateOcrGrayscale
            ? OnnxSessionFactory.ToGrayTensor(prepared, 1f / 255f, _models.PlateOcrMean, _models.PlateOcrStd)
            : OnnxSessionFactory.ToTensor(prepared, swapRb: true, 1f / 255f, _models.PlateOcrMean, _models.PlateOcrStd);

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });

        var output = results.First();
        var shape = output.AsTensor<float>().Dimensions.ToArray();
        var data = output.AsEnumerable<float>().ToArray();

        // Se espera [1, T, C] (PP-OCR) o [T, 1, C] (CRNN clásico).
        var (steps, classes, timeMajor) = shape.Length switch
        {
            3 when shape[0] == 1 => (shape[1], shape[2], false),
            3 => (shape[0], shape[2], true),
            2 => (shape[0], shape[1], false),
            _ => throw new InvalidOperationException(
                    $"Salida de OCR no soportada [{string.Join(",", shape)}]."),
        };

        // [1, T, C] y [T, 1, C] comparten la misma disposición lineal (t * C + c),
        // así que el decodificador es el mismo en ambos casos.
        _ = timeMajor;
        return GreedyDecode(data, steps, classes);
    }

    private Mat Preprocess(Mat plateBgr)
    {
        var targetH = _models.PlateOcrInputHeight > 0 ? _models.PlateOcrInputHeight : 48;
        var targetW = _models.PlateOcrInputWidth > 0 ? _models.PlateOcrInputWidth : 320;

        using var source = _models.PlateOcrGrayscale ? ToGray(plateBgr) : plateBgr.Clone();

        // Se conserva la relación de aspecto y se rellena por la derecha, como PaddleOCR.
        var ratio = (double)source.Width / source.Height;
        var scaledW = Math.Clamp((int)Math.Ceiling(targetH * ratio), 1, targetW);

        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(scaledW, targetH), 0, 0, InterpolationFlags.Linear);

        if (scaledW == targetW) return resized.Clone();

        var padded = new Mat();
        Cv2.CopyMakeBorder(resized, padded, 0, 0, 0, targetW - scaledW, BorderTypes.Constant, Scalar.All(0));
        return padded;
    }

    private static Mat ToGray(Mat src)
    {
        var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private PlateReading GreedyDecode(float[] data, int steps, int classes)
    {
        var sb = new StringBuilder();
        double confidenceSum = 0;
        var kept = 0;
        var previousIndex = -1;

        var probs = new float[classes];

        for (var t = 0; t < steps; t++)
        {
            var offset = t * classes;
            if (offset + classes > data.Length) break;

            for (var c = 0; c < classes; c++)
                probs[c] = data[offset + c];

            // Si la fila no suma 1 son logits y hay que pasarlos por softmax.
            NormalizeIfNeeded(probs);

            var bestIndex = 0;
            var bestProb = probs[0];
            for (var c = 1; c < classes; c++)
            {
                if (probs[c] > bestProb) { bestProb = probs[c]; bestIndex = c; }
            }

            var isBlank = _models.PlateOcrBlankFirst ? bestIndex == 0 : bestIndex == classes - 1;

            if (!isBlank && bestIndex != previousIndex)
            {
                var ch = MapCharacter(bestIndex);
                if (ch is not null)
                {
                    sb.Append(ch);
                    confidenceSum += bestProb;
                    kept++;
                }
            }

            previousIndex = bestIndex;
        }

        var confidence = kept == 0 ? 0f : (float)(confidenceSum / kept);
        return new PlateReading(sb.ToString(), confidence);
    }

    private string? MapCharacter(int index)
    {
        var charIndex = _models.PlateOcrBlankFirst ? index - 1 : index;
        if (charIndex < 0 || charIndex >= _charset.Length) return null;

        var value = _charset[charIndex];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static void NormalizeIfNeeded(float[] probs)
    {
        double sum = 0;
        var negative = false;
        foreach (var p in probs)
        {
            sum += p;
            if (p < 0) negative = true;
        }

        if (!negative && Math.Abs(sum - 1.0) < 0.05) return; // ya son probabilidades

        var max = probs[0];
        for (var i = 1; i < probs.Length; i++) if (probs[i] > max) max = probs[i];

        double expSum = 0;
        for (var i = 0; i < probs.Length; i++)
        {
            var e = Math.Exp(probs[i] - max);
            probs[i] = (float)e;
            expSum += e;
        }

        if (expSum <= 0) return;
        for (var i = 0; i < probs.Length; i++) probs[i] = (float)(probs[i] / expSum);
    }

    private static string[] LoadCharset(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException(
                $"No se encuentra el diccionario del OCR «{path}». Es un fichero de texto con un carácter por línea.", path);

        // No se recortan los espacios: hay diccionarios que incluyen el carácter espacio.
        return File.ReadAllLines(path)
                   .Select(l => l.TrimEnd('\r', '\n'))
                   .Where(l => l.Length > 0)
                   .ToArray();
    }

    public void Dispose() => _session.Dispose();
}
