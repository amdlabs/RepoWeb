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

    /// <summary>
    /// Confusiones típicas del OCR: en la zona de letras un «0» es casi siempre una
    /// «O», y en la zona de dígitos una «I» es un «1». Conocer el patrón permite
    /// resolverlas sin ambigüedad.
    /// </summary>
    private static readonly Dictionary<char, char> ADigito = new()
    {
        ['O'] = '0', ['Q'] = '0', ['D'] = '0',
        ['I'] = '1', ['L'] = '1', ['T'] = '7',
        ['Z'] = '2', ['S'] = '5', ['B'] = '8', ['G'] = '6', ['A'] = '4',
    };

    private static readonly Dictionary<char, char> ALetra = new()
    {
        ['0'] = 'O', ['1'] = 'I', ['2'] = 'Z', ['5'] = 'S', ['8'] = 'B', ['6'] = 'G', ['4'] = 'A',
    };

    /// <summary>
    /// Ajusta una lectura al patrón corrigiendo sólo caracteres confundibles: si la
    /// longitud es la correcta pero hay un dígito donde debe ir letra (o al revés),
    /// se sustituye por su equivalente. Devuelve null si no se puede encajar.
    /// Es el aprendizaje inmediato del OCR: «ABC12E4» → «ABC1234».
    /// </summary>
    public static string? SnapToPattern(string normalized, int letters = 3, int digits = 4)
    {
        if (normalized.Length != letters + digits) return null;

        var chars = normalized.ToCharArray();

        for (var i = 0; i < letters; i++)
        {
            if (char.IsAsciiLetterUpper(chars[i])) continue;
            if (!ALetra.TryGetValue(chars[i], out var letra)) return null;
            chars[i] = letra;
        }

        for (var i = letters; i < chars.Length; i++)
        {
            if (char.IsAsciiDigit(chars[i])) continue;
            if (!ADigito.TryGetValue(chars[i], out var digito)) return null;
            chars[i] = digito;
        }

        return new string(chars);
    }

    /// <summary>
    /// Patrón uruguayo actual: tres letras seguidas de cuatro dígitos (ABC1234).
    /// Se exige sobre el texto ya normalizado; una lectura que no lo cumpla se
    /// descarta en lugar de guardar una matrícula inventada.
    /// </summary>
    public static bool MatchesLetterDigitPattern(string normalized, int letters = 3, int digits = 4)
    {
        if (normalized.Length != letters + digits) return false;

        for (var i = 0; i < letters; i++)
            if (!char.IsAsciiLetterUpper(normalized[i])) return false;

        for (var i = letters; i < normalized.Length; i++)
            if (!char.IsAsciiDigit(normalized[i])) return false;

        return true;
    }
}
