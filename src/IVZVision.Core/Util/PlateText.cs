using System.Text;
using System.Text.RegularExpressions;
using IVZVision.Core.Configuration;

namespace IVZVision.Core.Util;

/// <summary>Normalización, validación y corrección de matrículas.</summary>
public static class PlateText
{
    /// <summary>Patrón de cada formato soportado.</summary>
    private static readonly Dictionary<PlateFormat, string> Patterns = new()
    {
        [PlateFormat.Uruguay] = "^[A-Z]{3}[0-9]{4}$",
        [PlateFormat.Spain] = "^[0-9]{4}[A-Z]{3}$",
        [PlateFormat.Argentina] = "^[A-Z]{2}[0-9]{3}[A-Z]{2}$",
        [PlateFormat.Mercosur] = "^[A-Z]{3}[0-9][A-Z0-9][0-9]{2}$",
    };

    private static readonly Dictionary<PlateFormat, Regex> Compiled = Patterns.ToDictionary(
        kv => kv.Key,
        kv => new Regex(kv.Value, RegexOptions.Compiled | RegexOptions.CultureInvariant));

    /// <summary>Dígitos que el OCR confunde con letras, y su equivalente.</summary>
    private static readonly Dictionary<char, char> DigitToLetter = new()
    {
        ['0'] = 'O', ['1'] = 'I', ['2'] = 'Z', ['5'] = 'S', ['6'] = 'G', ['8'] = 'B',
    };

    private static readonly Dictionary<char, char> LetterToDigit = new()
    {
        ['O'] = '0', ['Q'] = '0', ['D'] = '0', ['I'] = '1', ['L'] = '1', ['Z'] = '2',
        ['S'] = '5', ['G'] = '6', ['B'] = '8', ['A'] = '4',
    };

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

    /// <summary>Patrón efectivo del formato configurado, o null si no hay uno fijo.</summary>
    public static string? PatternFor(PlateFormat format, string? customPattern)
    {
        if (format == PlateFormat.Custom)
            return string.IsNullOrWhiteSpace(customPattern) ? null : customPattern.Trim();

        return Patterns.TryGetValue(format, out var pattern) ? pattern : null;
    }

    /// <summary>
    /// Corrige las confusiones típicas del OCR aprovechando que el formato fija qué
    /// posiciones son letras y cuáles números. En Uruguay (3 letras + 4 números) una
    /// lectura "5AB1234" se convierte en "SAB1234".
    /// </summary>
    public static string CoerceToFormat(string normalized, PlateFormat format, string? customPattern)
    {
        if (normalized.Length == 0) return normalized;

        // El ajuste sólo es seguro con formatos de longitud y composición fijas.
        var layout = LayoutFor(format);
        if (layout is null || layout.Length != normalized.Length) return normalized;

        var chars = normalized.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            switch (layout[i])
            {
                case 'A' when char.IsDigit(c):
                    if (DigitToLetter.TryGetValue(c, out var letter)) chars[i] = letter;
                    break;
                case '9' when char.IsAsciiLetterUpper(c):
                    if (LetterToDigit.TryGetValue(c, out var digit)) chars[i] = digit;
                    break;
            }
        }

        var candidate = new string(chars);
        return IsValidForFormat(candidate, format, customPattern) ? candidate : normalized;
    }

    /// <summary>
    /// Disposición del formato: 'A' = letra obligatoria, '9' = dígito obligatorio,
    /// '*' = cualquiera. Null si el formato no tiene una estructura fija conocida.
    /// </summary>
    private static string? LayoutFor(PlateFormat format) => format switch
    {
        PlateFormat.Uruguay => "AAA9999",
        PlateFormat.Spain => "9999AAA",
        PlateFormat.Argentina => "AA999AA",
        PlateFormat.Mercosur => "AAA9*99",
        _ => null,
    };

    public static bool IsValidForFormat(string normalized, PlateFormat format, string? customPattern)
    {
        if (format == PlateFormat.Custom)
        {
            if (string.IsNullOrWhiteSpace(customPattern)) return true;
            try
            {
                return Regex.IsMatch(normalized, customPattern.Trim(),
                                     RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return true;  // patrón mal escrito: no se bloquea la lectura
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return !Compiled.TryGetValue(format, out var regex) || regex.IsMatch(normalized);
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
    /// Validación completa: forma general y, si hay un formato de país configurado,
    /// que la matrícula lo cumpla.
    /// </summary>
    public static bool LooksValid(string normalized, PlateFormat format, string? customPattern,
                                  int minChars, int maxChars)
    {
        if (!LooksValid(normalized, minChars, maxChars)) return false;
        return format == PlateFormat.Generic || IsValidForFormat(normalized, format, customPattern);
    }

    /// <summary>Presentación con separador: "SAB1234" → "SAB 1234" en Uruguay.</summary>
    public static string Format(string normalized, PlateFormat format) => format switch
    {
        PlateFormat.Uruguay when normalized.Length == 7 => $"{normalized[..3]} {normalized[3..]}",
        PlateFormat.Spain when normalized.Length == 7 => $"{normalized[..4]} {normalized[4..]}",
        PlateFormat.Argentina when normalized.Length == 7 => $"{normalized[..2]} {normalized[2..5]} {normalized[5..]}",
        _ => normalized,
    };
}
