namespace IVZVision.Core.Util;

public static class VectorMath
{
    /// <summary>Normaliza el vector a norma L2 = 1 (in-place sobre una copia nueva).</summary>
    public static float[] L2Normalize(ReadOnlySpan<float> v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];

        var norm = Math.Sqrt(sum);
        var result = new float[v.Length];
        if (norm < 1e-9) return result;

        for (var i = 0; i < v.Length; i++)
            result[i] = (float)(v[i] / norm);

        return result;
    }

    /// <summary>Similitud coseno. Si los vectores ya están normalizados equivale al producto escalar.</summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return -1f;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom < 1e-9 ? -1f : (float)(dot / denom);
    }

    public static byte[] ToBytes(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < sizeof(float)) return Array.Empty<float>();
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * sizeof(float));
        return floats;
    }
}
