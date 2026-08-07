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
                    // Requisitos del proveedor DirectML según la documentación de ONNX Runtime.
                    options.EnableMemoryPattern = false;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
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

    /// <summary>
    /// Prepara la entrada respetando el tipo que espera el modelo: si el ONNX está
    /// exportado en media precisión (float16) el tensor se convierte automáticamente.
    /// </summary>
    public static NamedOnnxValue CreateInput(InferenceSession session, string inputName, DenseTensor<float> tensor)
    {
        var expectsHalf = session.InputMetadata.TryGetValue(inputName, out var meta)
                          && meta.ElementDataType == TensorElementType.Float16;

        if (!expectsHalf)
            return NamedOnnxValue.CreateFromTensor(inputName, tensor);

        var source = tensor.Buffer.Span;
        var half = new DenseTensor<Float16>(tensor.Dimensions);
        var target = half.Buffer.Span;
        for (var i = 0; i < source.Length; i++)
            target[i] = (Float16)source[i];

        return NamedOnnxValue.CreateFromTensor(inputName, half);
    }

    /// <summary>
    /// Lee una salida como float con su forma, convirtiendo desde float16 si el
    /// modelo produce media precisión.
    /// </summary>
    public static float[] ToFloatArray(DisposableNamedOnnxValue output, out int[] shape)
    {
        if (output.ElementType == TensorElementType.Float16)
        {
            var halfTensor = output.AsTensor<Float16>();
            shape = halfTensor.Dimensions.ToArray();

            var data = new float[halfTensor.Length];
            var i = 0;
            foreach (var v in halfTensor)
                data[i++] = (float)v;
            return data;
        }

        var tensor = output.AsTensor<float>();
        shape = tensor.Dimensions.ToArray();
        return tensor.ToArray();
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
