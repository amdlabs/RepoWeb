using System.Text;

namespace IVZVision.Core.Util;

/// <summary>Normalización de matrículas para poder compararlas contra la base de datos.</summary>
public static class PlateText
{
    /// <summary>Mayúsculas, sin espacios ni guiones y sin acentos. "1234-abc" → "1234ABC".</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var normalized = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>Comprueba que la lectura tiene una pinta razonable de matrícula.</summary>
    public static bool LooksValid(string normalized, int minChars, int maxChars)
    {
        if (normalized.Length < minChars || normalized.Length > maxChars) return false;

        var hasDigit = false;
        foreach (var ch in normalized)
        {
            if (char.IsDigit(ch)) { hasDigit = true; }
            else if (!char.IsAsciiLetterUpper(ch)) return false;
        }

        return hasDigit;
    }
}
