using System.Text;

namespace IVZVision.Web.Services;

/// <summary>
/// Dibuja una matrícula uruguaya (formato Mercosur) con el texto leído: banda azul
/// superior con «URUGUAY», la bandera y los caracteres en negro sobre fondo blanco.
/// Se genera como SVG para que se vea nítida a cualquier tamaño.
/// </summary>
public static class PlateImageBuilder
{
    public static string BuildSvg(string plate)
    {
        var text = FormatPlate(plate);

        var sb = new StringBuilder();
        sb.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 140' width='400' height='140' role='img'>");
        sb.Append($"<title>Matrícula {Escape(text)}</title>");

        // Cuerpo blanco con borde.
        sb.Append("<rect x='2' y='2' width='396' height='136' rx='12' fill='#ffffff' stroke='#111' stroke-width='3'/>");

        // Banda azul superior del Mercosur.
        sb.Append("<path d='M4 14a10 10 0 0 1 10-10h372a10 10 0 0 1 10 10v30H4z' fill='#0a3fa8'/>");
        sb.Append("<text x='200' y='36' font-family='Arial, Helvetica, sans-serif' font-size='26' font-weight='bold' " +
                  "fill='#ffffff' text-anchor='middle' letter-spacing='3'>URUGUAY</text>");
        sb.Append("<text x='12' y='39' font-family='Arial, Helvetica, sans-serif' font-size='9' fill='#ffffff'>MERCOSUR</text>");

        // Bandera de Uruguay (esquematizada) en la esquina derecha de la banda.
        sb.Append("<g transform='translate(345,8)'>");
        sb.Append("<rect width='46' height='30' fill='#ffffff' stroke='#0a3fa8' stroke-width='1'/>");
        for (var i = 0; i < 4; i++)
            sb.Append($"<rect x='18' y='{i * 8.5:0.##}' width='28' height='4.2' fill='#0a3fa8'/>");
        sb.Append("<rect width='18' height='17' fill='#ffffff'/>");
        sb.Append("<circle cx='9' cy='8.5' r='5.2' fill='#f6b40e' stroke='#8a6a00' stroke-width='0.8'/>");
        sb.Append("</g>");

        // Caracteres de la matrícula.
        sb.Append($"<text x='200' y='115' font-family='Arial Black, Arial, Helvetica, sans-serif' font-size='62' " +
                  $"font-weight='900' fill='#111111' text-anchor='middle' letter-spacing='2'>{Escape(text)}</text>");

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Separa letras y números al estilo uruguayo: «ZME2015» → «ZME 2015».</summary>
    private static string FormatPlate(string plate)
    {
        var clean = new string((plate ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length == 0) return "—";
        if (clean.Contains(' ')) return clean;

        var split = -1;
        for (var i = 1; i < clean.Length; i++)
        {
            if (char.IsLetter(clean[i - 1]) && char.IsDigit(clean[i])) { split = i; break; }
        }

        return split > 0 ? clean[..split] + " " + clean[split..] : clean;
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
