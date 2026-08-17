using IVZVision.Core.Detection;
using OpenCvSharp;

namespace IVZVision.Vision.Imaging;

/// <summary>
/// Reescalado que conserva la relación de aspecto rellenando con gris el sobrante.
/// Guarda la transformación para poder devolver las coordenadas al fotograma original.
/// </summary>
public readonly record struct LetterboxTransform(float Scale, int PadX, int PadY, int SourceWidth, int SourceHeight)
{
    /// <summary>Convierte un cuadro en coordenadas de la imagen de red a coordenadas del fotograma.</summary>
    public BoxF ToSource(float x, float y, float w, float h)
    {
        var sx = (x - PadX) / Scale;
        var sy = (y - PadY) / Scale;
        var sw = w / Scale;
        var sh = h / Scale;
        return new BoxF(sx, sy, sw, sh).ClampTo(SourceWidth, SourceHeight);
    }

    public (float X, float Y) PointToSource(float x, float y)
        => ((x - PadX) / Scale, (y - PadY) / Scale);
}

public static class ImageOps
{
    private static readonly Scalar PadColor = new(114, 114, 114);

    /// <summary>Reescala <paramref name="src"/> a <paramref name="width"/>x<paramref name="height"/> con relleno.</summary>
    public static Mat Letterbox(Mat src, int width, int height, out LetterboxTransform transform)
    {
        var scale = Math.Min((float)width / src.Width, (float)height / src.Height);
        var newW = Math.Max(1, (int)Math.Round(src.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(src.Height * scale));

        var padX = (width - newW) / 2;
        var padY = (height - newH) / 2;

        transform = new LetterboxTransform(scale, padX, padY, src.Width, src.Height);

        using var resized = new Mat();
        Cv2.Resize(src, resized, new Size(newW, newH), 0, 0, InterpolationFlags.Linear);

        var dst = new Mat();
        Cv2.CopyMakeBorder(resized, dst,
            padY, height - newH - padY,
            padX, width - newW - padX,
            BorderTypes.Constant, PadColor);

        return dst;
    }

    /// <summary>Recorta con seguridad: nunca sale del fotograma y nunca devuelve un Mat vacío.</summary>
    public static Mat? SafeCrop(Mat src, BoxF box)
    {
        var clamped = box.ClampTo(src.Width, src.Height);
        var rect = new Rect(
            (int)Math.Floor(clamped.X),
            (int)Math.Floor(clamped.Y),
            (int)Math.Round(clamped.Width),
            (int)Math.Round(clamped.Height));

        if (rect.Width < 2 || rect.Height < 2) return null;
        if (rect.X + rect.Width > src.Width) rect.Width = src.Width - rect.X;
        if (rect.Y + rect.Height > src.Height) rect.Height = src.Height - rect.Y;
        if (rect.Width < 2 || rect.Height < 2) return null;

        return new Mat(src, rect).Clone();
    }

    public static byte[] EncodeJpeg(Mat image, int quality)
    {
        Cv2.ImEncode(".jpg", image, out var buffer,
            new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, Math.Clamp(quality, 1, 100)) });
        return buffer;
    }

    /// <summary>Supresión de no-máximos sobre cuadros ya ordenables por puntuación.</summary>
    public static List<int> NonMaxSuppression(IReadOnlyList<BoxF> boxes, IReadOnlyList<float> scores, float iouThreshold)
    {
        var order = Enumerable.Range(0, boxes.Count).OrderByDescending(i => scores[i]).ToList();
        var keep = new List<int>();
        var suppressed = new bool[boxes.Count];

        foreach (var i in order)
        {
            if (suppressed[i]) continue;
            keep.Add(i);

            foreach (var j in order)
            {
                if (j == i || suppressed[j]) continue;
                if (BoxF.IntersectionOverUnion(boxes[i], boxes[j]) > iouThreshold)
                    suppressed[j] = true;
            }
        }

        return keep;
    }

    /// <summary>Reduce el fotograma si supera el ancho máximo de análisis. Devuelve el factor aplicado.</summary>
    public static Mat ScaleForAnalysis(Mat frame, int maxWidth, out float scale)
    {
        scale = 1f;
        if (maxWidth <= 0 || frame.Width <= maxWidth) return frame.Clone();

        scale = (float)maxWidth / frame.Width;
        var dst = new Mat();
        Cv2.Resize(frame, dst, new Size(maxWidth, Math.Max(1, (int)Math.Round(frame.Height * scale))),
                   0, 0, InterpolationFlags.Area);
        return dst;
    }
}
