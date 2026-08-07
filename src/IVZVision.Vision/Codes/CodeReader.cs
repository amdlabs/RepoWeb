using IVZVision.Core.Detection;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using ZXing;
using ZXing.Common;

namespace IVZVision.Vision.Codes;

public sealed record DetectedCode(string Value, string Format, BoxF Box);

/// <summary>
/// Lector de códigos QR y de barras. Usa ZXing, que es código gestionado puro: no
/// añade dependencias nativas ni hace falta descargar ningún modelo, así que esta
/// función está disponible siempre.
/// </summary>
public sealed class CodeReader
{
    private readonly BarcodeReaderGeneric _reader;
    private readonly ILogger _logger;

    public CodeReader(ILogger logger)
    {
        _logger = logger;
        _reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = new List<BarcodeFormat>
                {
                    BarcodeFormat.QR_CODE,
                    BarcodeFormat.DATA_MATRIX,
                    BarcodeFormat.AZTEC,
                    BarcodeFormat.PDF_417,
                    BarcodeFormat.EAN_13,
                    BarcodeFormat.EAN_8,
                    BarcodeFormat.UPC_A,
                    BarcodeFormat.UPC_E,
                    BarcodeFormat.CODE_128,
                    BarcodeFormat.CODE_39,
                    BarcodeFormat.CODE_93,
                    BarcodeFormat.ITF,
                    BarcodeFormat.CODABAR,
                },
            },
        };
    }

    public IReadOnlyList<DetectedCode> Read(Mat frameBgr, int minLength)
    {
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(frameBgr, gray, ColorConversionCodes.BGR2GRAY);

            var luminance = ToLuminanceSource(gray);
            var results = _reader.DecodeMultiple(luminance);
            if (results is null || results.Length == 0) return Array.Empty<DetectedCode>();

            var codes = new List<DetectedCode>(results.Length);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var result in results)
            {
                if (string.IsNullOrWhiteSpace(result.Text)) continue;
                if (result.Text.Length < minLength) continue;
                if (!seen.Add(result.Text)) continue;   // el mismo código leído dos veces

                codes.Add(new DetectedCode(
                    result.Text.Trim(),
                    result.BarcodeFormat.ToString(),
                    BoxFromPoints(result.ResultPoints, frameBgr.Width, frameBgr.Height)));
            }

            return codes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error al leer códigos del fotograma");
            return Array.Empty<DetectedCode>();
        }
    }

    /// <summary>Construye la fuente de luminancia de ZXing a partir de un Mat en escala de grises.</summary>
    private static RGBLuminanceSource ToLuminanceSource(Mat gray)
    {
        // Un Mat recortado comparte memoria con relleno al final de cada fila,
        // así que se clona antes de copiar en bloque.
        using var owned = gray.IsContinuous() ? null : gray.Clone();
        var source = owned ?? gray;

        var bytes = new byte[source.Width * source.Height];
        System.Runtime.InteropServices.Marshal.Copy(source.Data, bytes, 0, bytes.Length);

        return new RGBLuminanceSource(bytes, source.Width, source.Height,
                                      RGBLuminanceSource.BitmapFormat.Gray8);
    }

    /// <summary>
    /// ZXing devuelve los puntos de referencia del código (esquinas o extremos),
    /// no un rectángulo: se calcula el cuadro que los engloba.
    /// </summary>
    private static BoxF BoxFromPoints(ResultPoint[]? points, int frameWidth, int frameHeight)
    {
        if (points is null || points.Length == 0)
            return new BoxF(0, 0, 0, 0);

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);

        // Con un código de barras 1D los puntos están sólo en una línea horizontal:
        // se le da algo de alto para que el cuadrante sea visible.
        var height = Math.Max(maxY - minY, frameHeight * 0.02f);
        var width = Math.Max(maxX - minX, 8f);

        return new BoxF(minX, minY - height / 2f, width, height)
            .Expand(0.08f, frameWidth, frameHeight);
    }
}
