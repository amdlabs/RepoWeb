using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace IVZVision.Vision.Text;

/// <summary>Región de texto localizada, con su rectángulo girado para poder enderezarla.</summary>
public sealed record TextRegion(BoxF Box, RotatedRect Rotated, float Score);

/// <summary>
/// Detector de texto tipo DB (Differentiable Binarization), el mismo esquema que el
/// modelo <c>det</c> de PP-OCR: la red devuelve un mapa de probabilidad y las cajas
/// se obtienen binarizando, buscando contornos y expandiéndolos.
/// </summary>
public sealed class DbTextDetector : IDisposable
{
    /// <summary>
    /// Factor de expansión de las cajas. La red predice el núcleo del texto, más
    /// estrecho que el texto real, así que hay que dilatarlo para no cortar letras.
    /// </summary>
    private const double UnclipRatio = 1.6;

    private const int MinRegionSide = 6;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _maxSide;
    private readonly ILogger _logger;

    // Normalización ImageNet, la que usa PP-OCR en el detector.
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    public DbTextDetector(string modelPath, ModelsConfig models, ILogger logger)
    {
        _logger = logger;
        _session = OnnxSessionFactory.Create(modelPath, models, logger);
        _inputName = _session.InputMetadata.Keys.First();
        _maxSide = models.TextDetectorMaxSide > 0 ? models.TextDetectorMaxSide : 960;

        _logger.LogInformation("Detector de texto cargado (lado máximo {Side}px) desde {Path}", _maxSide, modelPath);
    }

    public IReadOnlyList<TextRegion> Detect(Mat frameBgr, float threshold)
    {
        // La red exige lados múltiplos de 32.
        var scale = Math.Min(1.0, (double)_maxSide / Math.Max(frameBgr.Width, frameBgr.Height));
        var width = RoundTo32((int)Math.Round(frameBgr.Width * scale));
        var height = RoundTo32((int)Math.Round(frameBgr.Height * scale));

        using var resized = new Mat();
        Cv2.Resize(frameBgr, resized, new Size(width, height), 0, 0, InterpolationFlags.Linear);

        var tensor = BuildTensor(resized);

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });

        var output = results.First();
        var shape = output.AsTensor<float>().Dimensions.ToArray();
        var data = output.AsEnumerable<float>().ToArray();

        // Se espera [1, 1, H, W].
        var mapHeight = shape.Length >= 2 ? shape[^2] : height;
        var mapWidth = shape.Length >= 1 ? shape[^1] : width;
        if (mapHeight * mapWidth > data.Length) return Array.Empty<TextRegion>();

        using var probability = new Mat(mapHeight, mapWidth, MatType.CV_32FC1);
        System.Runtime.InteropServices.Marshal.Copy(data, 0, probability.Data, mapHeight * mapWidth);

        using var binary = new Mat();
        Cv2.Threshold(probability, binary, threshold, 255, ThresholdTypes.Binary);

        using var mask = new Mat();
        binary.ConvertTo(mask, MatType.CV_8UC1);

        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        var scaleX = (float)frameBgr.Width / mapWidth;
        var scaleY = (float)frameBgr.Height / mapHeight;

        var regions = new List<TextRegion>();

        foreach (var contour in contours)
        {
            if (contour.Length < 4) continue;

            var rect = Cv2.MinAreaRect(contour);
            if (Math.Min(rect.Size.Width, rect.Size.Height) < MinRegionSide) continue;

            var score = MeanScore(probability, contour);
            if (score < threshold) continue;

            var expanded = Unclip(rect);

            // Del mapa de probabilidad a coordenadas del fotograma original.
            var scaled = new RotatedRect(
                new Point2f(expanded.Center.X * scaleX, expanded.Center.Y * scaleY),
                new Size2f(expanded.Size.Width * scaleX, expanded.Size.Height * scaleY),
                expanded.Angle);

            var bounding = ToBox(scaled).ClampTo(frameBgr.Width, frameBgr.Height);
            if (bounding.Width < MinRegionSide || bounding.Height < MinRegionSide) continue;

            regions.Add(new TextRegion(bounding, scaled, score));
        }

        return regions.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// Endereza la región para que el reconocedor reciba el texto horizontal.
    /// Sin esto, un cartel inclinado se lee muy mal.
    /// </summary>
    public static Mat? CropUpright(Mat frameBgr, TextRegion region)
    {
        var rect = region.Rotated;
        var width = (int)Math.Round(rect.Size.Width);
        var height = (int)Math.Round(rect.Size.Height);
        if (width < 2 || height < 2) return null;

        var angle = rect.Angle;

        // OpenCV devuelve el ángulo en (-90, 0]: si el rectángulo está "de pie" hay
        // que girarlo un cuarto de vuelta e intercambiar los lados.
        if (width < height)
        {
            (width, height) = (height, width);
            angle += 90;
        }

        using var rotation = Cv2.GetRotationMatrix2D(rect.Center, angle, 1.0);
        using var rotated = new Mat();
        Cv2.WarpAffine(frameBgr, rotated, rotation, frameBgr.Size(), InterpolationFlags.Linear);

        var size = new Size(width, height);
        var cropped = new Mat();

        try
        {
            Cv2.GetRectSubPix(rotated, size, rect.Center, cropped);
            return cropped.Empty() ? null : cropped;
        }
        catch (OpenCVException)
        {
            cropped.Dispose();
            return null;
        }
    }

    private Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float> BuildTensor(Mat bgr)
    {
        using var owned = bgr.IsContinuous() ? null : bgr.Clone();
        var src = owned ?? bgr;

        var height = src.Height;
        var width = src.Width;
        var channels = src.Channels();
        var plane = height * width;

        var tensor = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(new[] { 1, 3, height, width });
        var buffer = tensor.Buffer.Span;

        var bytes = new byte[plane * channels];
        System.Runtime.InteropServices.Marshal.Copy(src.Data, bytes, 0, bytes.Length);

        for (var i = 0; i < plane; i++)
        {
            var px = i * channels;
            // El Mat viene en BGR y el modelo espera RGB.
            var r = bytes[px + 2] / 255f;
            var g = bytes[px + 1] / 255f;
            var b = bytes[px] / 255f;

            buffer[i] = (r - Mean[0]) / Std[0];
            buffer[plane + i] = (g - Mean[1]) / Std[1];
            buffer[2 * plane + i] = (b - Mean[2]) / Std[2];
        }

        return tensor;
    }

    /// <summary>
    /// Dilata el rectángulo la distancia estándar de DB: área · ratio / perímetro.
    /// Es la aproximación habitual al desplazamiento de polígono de PP-OCR.
    /// </summary>
    private static RotatedRect Unclip(RotatedRect rect)
    {
        var area = rect.Size.Width * rect.Size.Height;
        var perimeter = 2 * (rect.Size.Width + rect.Size.Height);
        if (perimeter <= 0) return rect;

        var distance = (float)(area * UnclipRatio / perimeter);

        return new RotatedRect(rect.Center,
                               new Size2f(rect.Size.Width + 2 * distance, rect.Size.Height + 2 * distance),
                               rect.Angle);
    }

    /// <summary>Confianza de la región: probabilidad media dentro de su contorno.</summary>
    private static float MeanScore(Mat probability, Point[] contour)
    {
        var rect = Cv2.BoundingRect(contour);
        rect = rect.Intersect(new Rect(0, 0, probability.Width, probability.Height));
        if (rect.Width <= 0 || rect.Height <= 0) return 0;

        using var region = new Mat(probability, rect);
        using var mask = Mat.Zeros(rect.Height, rect.Width, MatType.CV_8UC1).ToMat();

        var shifted = contour.Select(p => new Point(p.X - rect.X, p.Y - rect.Y)).ToArray();
        Cv2.FillPoly(mask, new[] { shifted }, Scalar.All(255));

        return (float)Cv2.Mean(region, mask).Val0;
    }

    private static BoxF ToBox(RotatedRect rect)
    {
        var points = rect.Points();
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return new BoxF(minX, minY, maxX - minX, maxY - minY);
    }

    private static int RoundTo32(int value) => Math.Max(32, (int)Math.Round(value / 32.0) * 32);

    public void Dispose() => _session.Dispose();
}
