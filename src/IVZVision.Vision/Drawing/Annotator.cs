using System.Globalization;
using System.Text;
using IVZVision.Core.Detection;
using OpenCvSharp;

namespace IVZVision.Vision.Drawing;

/// <summary>Dibuja sobre el fotograma el cuadrante y la etiqueta de cada objeto identificado.</summary>
public static class Annotator
{
    // Colores en BGR, que es el orden que usa OpenCV.
    private static readonly Scalar Known = new(90, 200, 60);            // verde
    private static readonly Scalar KnownRestricted = new(30, 170, 245); // ámbar
    private static readonly Scalar Unknown = new(60, 60, 235);          // rojo
    private static readonly Scalar Neutral = new(200, 170, 90);         // azul claro: objetos y texto
    private static readonly Scalar CodeColor = new(190, 120, 220);      // violeta: códigos
    private static readonly Scalar AlertColor = new(50, 50, 255);       // rojo intenso: alertas
    private static readonly Scalar ZoneColor = new(80, 200, 255);       // amarillo: zona restringida
    private static readonly Scalar TextColor = Scalar.White;

    public static void Draw(Mat frame, IEnumerable<Observation> observations)
    {
        // Las alertas se pintan al final para que queden por encima de todo lo demás.
        foreach (var obs in observations.OrderBy(o => o.Kind == ObservationKind.Activity ? 1 : 0))
        {
            var color = ColorFor(obs);

            var box = obs.Box.ClampTo(frame.Width, frame.Height);
            var rect = new Rect((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);
            if (rect.Width < 2 || rect.Height < 2) continue;

            var baseThickness = Math.Max(2, frame.Width / 640);
            var thickness = obs.Kind == ObservationKind.Activity ? baseThickness + 1 : baseThickness;

            Cv2.Rectangle(frame, rect, color, thickness, LineTypes.AntiAlias);

            DrawLabel(frame, rect, BuildLabel(obs), color, thickness);
        }
    }

    /// <summary>Marca la zona restringida con un trazo discontinuo para no tapar la escena.</summary>
    public static void DrawRestrictedZone(Mat frame, BoxF zone)
    {
        var box = zone.ClampTo(frame.Width, frame.Height);
        if (box.Width < 4 || box.Height < 4) return;

        var thickness = Math.Max(1, frame.Width / 900);
        var dash = Math.Max(8, frame.Width / 80);

        var left = (int)box.X;
        var top = (int)box.Y;
        var right = (int)box.Right;
        var bottom = (int)box.Bottom;

        for (var x = left; x < right; x += dash * 2)
        {
            var end = Math.Min(x + dash, right);
            Cv2.Line(frame, new Point(x, top), new Point(end, top), ZoneColor, thickness);
            Cv2.Line(frame, new Point(x, bottom), new Point(end, bottom), ZoneColor, thickness);
        }

        for (var y = top; y < bottom; y += dash * 2)
        {
            var end = Math.Min(y + dash, bottom);
            Cv2.Line(frame, new Point(left, y), new Point(left, end), ZoneColor, thickness);
            Cv2.Line(frame, new Point(right, y), new Point(right, end), ZoneColor, thickness);
        }

        Cv2.PutText(frame, "ZONA RESTRINGIDA", new Point(left + 6, Math.Max(16, top - 6)),
            HersheyFonts.HersheySimplex, Math.Clamp(frame.Width / 1600.0, 0.4, 0.8), ZoneColor, thickness, LineTypes.AntiAlias);
    }

    private static Scalar ColorFor(Observation obs) => obs.Kind switch
    {
        ObservationKind.Activity => obs.Severity == AlertSeverity.Info ? KnownRestricted : AlertColor,
        ObservationKind.Code => CodeColor,
        ObservationKind.Text => Neutral,
        ObservationKind.Object when !obs.Match.IsKnown => Neutral,
        _ => obs.Match.IsKnown ? (obs.Match.IsAuthorized ? Known : KnownRestricted) : Unknown,
    };

    private static string BuildLabel(Observation obs)
    {
        var text = obs.Kind switch
        {
            ObservationKind.Activity => $"! {Observation.DescribeActivity(obs.Activity)}",
            ObservationKind.Code => $"COD {Shorten(obs.CodeValue, 24)}",
            ObservationKind.Text => $"TXT {Shorten(obs.TextValue, 28)}",
            ObservationKind.Object => $"OBJ {obs.DisplayLabel} {Percent(obs.DetectionScore)}",
            ObservationKind.Plate => $"MAT {obs.DisplayLabel} {Percent(obs.OcrConfidence ?? obs.DetectionScore)}",
            _ => $"ROS {obs.DisplayLabel} {Percent(obs.Match.IsKnown ? obs.Match.Score : obs.DetectionScore)}",
        };

        return ToAscii(text);
    }

    private static string Percent(float value) =>
        (value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Shorten(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max] + "...";
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
