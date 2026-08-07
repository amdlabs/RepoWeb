using IVZVision.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace IVZVision.Vision.Onnx;

public static class OnnxSessionFactory
{
    /// <summary>
    /// Abre un modelo ONNX con el proveedor de ejecución configurado. Si la GPU
    /// no está disponible se registra el problema y se continúa en CPU.
    /// </summary>
    public static InferenceSession Create(string modelPath, ModelsConfig cfg, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException($"No se encuentra el modelo ONNX «{modelPath}».", modelPath);

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        if (cfg.IntraOpThreads > 0)
            options.IntraOpNumThreads = cfg.IntraOpThreads;

        try
        {
            switch (cfg.ExecutionProvider)
            {
                case ExecutionProviderKind.Cuda:
                    options.AppendExecutionProvider_CUDA(cfg.GpuDeviceId);
                    break;
                case ExecutionProviderKind.DirectMl:
                    options.AppendExecutionProvider_DML(cfg.GpuDeviceId);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "No se pudo activar el proveedor {Provider}; se usará la CPU. " +
                "Instale el paquete Microsoft.ML.OnnxRuntime.Gpu/DirectML si necesita aceleración.",
                cfg.ExecutionProvider);
        }

        return new InferenceSession(modelPath, options);
    }

    /// <summary>Convierte un Mat BGR de 8 bits en un tensor NCHW.</summary>
    /// <param name="swapRb">true para reordenar a RGB (modelos entrenados con RGB).</param>
    /// <param name="scale">Factor aplicado al píxel antes de restar la media.</param>
    /// <param name="mean">Media restada tras escalar.</param>
    /// <param name="std">Divisor aplicado tras restar la media.</param>
    public static DenseTensor<float> ToTensor(Mat bgr, bool swapRb, float scale, float mean, float std)
    {
        // Un Mat recortado comparte memoria con el original y tiene relleno al final de
        // cada fila; clonarlo garantiza que la copia en bloque siguiente sea válida.
        Mat? owned = bgr.IsContinuous() ? null : bgr.Clone();
        var src = owned ?? bgr;

        try
        {
            var height = src.Height;
            var width = src.Width;
            var channels = src.Channels();

            var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });
            var buffer = tensor.Buffer.Span;
            var plane = height * width;

            var invStd = std == 0 ? 1f : 1f / std;

            var bytes = new byte[plane * channels];
            System.Runtime.InteropServices.Marshal.Copy(src.Data, bytes, 0, bytes.Length);

            for (var y = 0; y < height; y++)
            {
                var rowStart = y * width * channels;
                for (var x = 0; x < width; x++)
                {
                    var px = rowStart + x * channels;

                    float b = bytes[px];
                    float g = channels > 1 ? bytes[px + 1] : b;
                    float r = channels > 2 ? bytes[px + 2] : b;

                    var c0 = swapRb ? r : b;
                    var c2 = swapRb ? b : r;

                    var idx = y * width + x;
                    buffer[0 * plane + idx] = (c0 * scale - mean) * invStd;
                    buffer[1 * plane + idx] = (g * scale - mean) * invStd;
                    buffer[2 * plane + idx] = (c2 * scale - mean) * invStd;
                }
            }

            return tensor;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>Variante de un solo canal (escala de grises) para OCR entrenado en gris.</summary>
    public static DenseTensor<float> ToGrayTensor(Mat gray, float scale, float mean, float std)
    {
        Mat? owned = gray.IsContinuous() ? null : gray.Clone();
        var src = owned ?? gray;

        try
        {
            var height = src.Height;
            var width = src.Width;

            var tensor = new DenseTensor<float>(new[] { 1, 1, height, width });
            var buffer = tensor.Buffer.Span;
            var invStd = std == 0 ? 1f : 1f / std;

            var bytes = new byte[height * width];
            System.Runtime.InteropServices.Marshal.Copy(src.Data, bytes, 0, bytes.Length);

            for (var i = 0; i < bytes.Length; i++)
                buffer[i] = (bytes[i] * scale - mean) * invStd;

            return tensor;
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
