using OpenCvSharp;

namespace IVZVision.Vision.Faces;

/// <summary>
/// Alineación canónica de rostros a 112x112 usando los cinco puntos de referencia,
/// el mismo criterio que ArcFace/SFace. Sin alineación la identificación pierde
/// muchísima precisión con caras giradas.
/// </summary>
public static class FaceAligner
{
    public const int OutputSize = 112;

    /// <summary>Posición canónica de ojo izq., ojo der., nariz y comisuras en la imagen 112x112.</summary>
    private static readonly (float X, float Y)[] Reference =
    {
        (38.2946f, 51.6963f),
        (73.5318f, 51.5014f),
        (56.0252f, 71.7366f),
        (41.5493f, 92.3655f),
        (70.7299f, 92.2041f),
    };

    public static Mat Align(Mat frameBgr, (float X, float Y)[] landmarks)
    {
        var dst = new Mat();

        if (landmarks.Length < 5)
        {
            // Sin puntos suficientes se cae a un reescalado directo del recorte.
            Cv2.Resize(frameBgr, dst, new Size(OutputSize, OutputSize), 0, 0, InterpolationFlags.Linear);
            return dst;
        }

        using var m = SimilarityTransform(landmarks, Reference);
        Cv2.WarpAffine(frameBgr, dst, m, new Size(OutputSize, OutputSize),
                       InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        return dst;
    }

    /// <summary>
    /// Transformación de semejanza (rotación + escala uniforme + traslación) por
    /// mínimos cuadrados. Solución cerrada de Umeyama para 2D, sin SVD.
    /// </summary>
    private static Mat SimilarityTransform((float X, float Y)[] from, (float X, float Y)[] to)
    {
        var n = Math.Min(from.Length, to.Length);

        double mfx = 0, mfy = 0, mtx = 0, mty = 0;
        for (var i = 0; i < n; i++)
        {
            mfx += from[i].X; mfy += from[i].Y;
            mtx += to[i].X; mty += to[i].Y;
        }
        mfx /= n; mfy /= n; mtx /= n; mty /= n;

        // a y b codifican la matriz [[a, -b], [b, a]] = escala * rotación.
        double num = 0, cross = 0, den = 0;
        for (var i = 0; i < n; i++)
        {
            var fx = from[i].X - mfx;
            var fy = from[i].Y - mfy;
            var tx = to[i].X - mtx;
            var ty = to[i].Y - mty;

            num += fx * tx + fy * ty;
            cross += fx * ty - fy * tx;
            den += fx * fx + fy * fy;
        }

        double a, b;
        if (den < 1e-9)
        {
            a = 1; b = 0;
        }
        else
        {
            a = num / den;
            b = cross / den;
        }

        var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, a);
        m.Set(0, 1, -b);
        m.Set(0, 2, mtx - (a * mfx - b * mfy));
        m.Set(1, 0, b);
        m.Set(1, 1, a);
        m.Set(1, 2, mty - (b * mfx + a * mfy));

        return m;
    }
}
