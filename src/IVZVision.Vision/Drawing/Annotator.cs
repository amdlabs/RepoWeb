using System.Globalization;
using System.Text;
using IVZVision.Core.Detection;
using OpenCvSharp;

namespace IVZVision.Vision.Drawing;

/// <summary>Dibuja sobre el fotograma el cuadrante y la etiqueta de cada objeto identificado.</summary>
public static class Annotator
{
    private static readonly Scalar Known = new(90, 200, 60);        // verde  (BGR)
    private static readonly Scalar KnownRestricted = new(30, 170, 245); // ámbar
    private static readonly Scalar Unknown = new(60, 60, 235);      // rojo
    private static readonly Scalar SceneText = new(220, 200, 60);   // turquesa: texto leído
    private static readonly Scalar TextColor = Scalar.White;

    public static void Draw(Mat frame, IEnumerable<Observation> observations)
    {
        foreach (var obs in observations)
        {
            var color = obs.Kind == ObservationKind.Text
                ? SceneText
                : obs.Match.IsKnown
                    ? (obs.Match.IsAuthorized ? Known : KnownRestricted)
                    : Unknown;

            var box = obs.Box.ClampTo(frame.Width, frame.Height);
            var rect = new Rect((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);
            if (rect.Width < 2 || rect.Height < 2) continue;

            var thickness = Math.Max(2, frame.Width / 640);
            Cv2.Rectangle(frame, rect, color, thickness, LineTypes.AntiAlias);

            var label = BuildLabel(obs);
            DrawLabel(frame, rect, label, color, thickness);
        }
    }

    private static string BuildLabel(Observation obs)
    {
        var percent = obs.Kind == ObservationKind.Plate
            ? (obs.OcrConfidence ?? obs.DetectionScore)
            : (obs.Match.IsKnown ? obs.Match.Score : obs.DetectionScore);

        var prefix = obs.Kind switch
        {
            ObservationKind.Plate => "MAT",
            ObservationKind.Object => "OBJ",
            ObservationKind.Text => "TXT",
            _ => "ROS",
        };

        // Los textos leídos no llevan porcentaje: el rótulo ES el texto.
        var text = obs.Kind == ObservationKind.Text
            ? $"{prefix} {obs.DisplayLabel}"
            : $"{prefix} {obs.DisplayLabel} {percent * 100:0}%";
        return ToAscii(text);
    }

    private static void DrawLabel(Mat frame, Rect box, string label, Scalar color, int thickness)
    {
        var fontScale = Math.Clamp(frame.Width / 1400.0, 0.45, 1.0);
        var fontThickness = Math.Max(1, thickness - 1);

        var size = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, fontScale, fontThickness, out var baseline);

        var top = box.Y - size.Height - baseline - 6;
        var drawAbove = top >= 0;
        if (!drawAbove) top = box.Y + box.Height + 2;

        var bg = new Rect(box.X, Math.Max(0, top), size.Width + 10, size.Height + baseline + 6);
        if (bg.X + bg.Width > frame.Width) bg.X = Math.Max(0, frame.Width - bg.Width);
        if (bg.Y + bg.Height > frame.Height) bg.Y = Math.Max(0, frame.Height - bg.Height);

        Cv2.Rectangle(frame, bg, color, -1);
        Cv2.PutText(frame, label,
            new Point(bg.X + 5, bg.Y + size.Height + 3),
            HersheyFonts.HersheySimplex, fontScale, TextColor, fontThickness, LineTypes.AntiAlias);
    }

    /// <summary>
    /// Las fuentes Hershey de OpenCV sólo dibujan ASCII, así que los acentos y la ñ
    /// se transliteran para que no aparezcan como interrogantes sobre el vídeo.
    /// </summary>
    private static string ToAscii(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch <= 0x7E && ch >= 0x20 ? ch : '?');
        }

        return sb.ToString();
    }
}
