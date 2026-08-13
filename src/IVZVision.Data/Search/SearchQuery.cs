using System.Globalization;
using System.Text;
using IVZVision.Data.Entities;

namespace IVZVision.Data.Search;

/// <summary>Consulta estructurada resultante de interpretar un texto libre.</summary>
public sealed class SearchQuery
{
    public string? RawPrompt { get; set; }

    public RecognitionKind? Kind { get; set; }

    /// <summary>true = sólo identificados, false = sólo desconocidos, null = ambos.</summary>
    public bool? OnlyKnown { get; set; }

    /// <summary>Sólo sujetos identificados pero sin autorización de acceso.</summary>
    public bool OnlyUnauthorized { get; set; }

    /// <summary>Sólo alertas de actividad sospechosa.</summary>
    public bool OnlyAlerts { get; set; }

    public Guid? CameraId { get; set; }

    /// <summary>Nombre de cámara mencionado en el texto, si no se pudo resolver a un id.</summary>
    public string? CameraName { get; set; }

    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    /// <summary>Clase de objeto mencionada (person, dog, car…).</summary>
    public string? ObjectClass { get; set; }

    /// <summary>Palabras que deben aparecer en la etiqueta, la matrícula, el código o el texto.</summary>
    public string? FreeText { get; set; }

    public int Take { get; set; } = 50;

    /// <summary>Resumen legible de cómo se interpretó el texto, para mostrarlo al usuario.</summary>
    public string Describe()
    {
        var parts = new List<string>();

        if (Kind is not null) parts.Add($"tipo: {DescribeKind(Kind.Value)}");
        if (OnlyAlerts) parts.Add("sólo alertas");
        if (OnlyKnown == true) parts.Add("identificados");
        if (OnlyKnown == false) parts.Add("desconocidos");
        if (OnlyUnauthorized) parts.Add("sin autorización");
        if (!string.IsNullOrEmpty(ObjectClass)) parts.Add($"clase: {ObjectClass}");
        if (CameraName is not null) parts.Add($"cámara: {CameraName}");

        if (FromUtc is not null || ToUtc is not null)
        {
            var desde = FromUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) ?? "el principio";
            var hasta = ToUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) ?? "ahora";
            parts.Add($"desde {desde} hasta {hasta}");
        }

        if (!string.IsNullOrEmpty(FreeText)) parts.Add($"texto: «{FreeText}»");

        return parts.Count == 0 ? "todo el histórico" : string.Join(" · ", parts);
    }

    public static string DescribeKind(RecognitionKind kind) => kind switch
    {
        RecognitionKind.Face => "rostros",
        RecognitionKind.Plate => "matrículas",
        RecognitionKind.Object => "objetos",
        RecognitionKind.Code => "códigos",
        RecognitionKind.Text => "texto",
        RecognitionKind.Activity => "alertas",
        _ => kind.ToString(),
    };
}

/// <summary>
/// Traduce una frase en castellano a una <see cref="SearchQuery"/>. Es un analizador
/// determinista por palabras clave: funciona sin conexión y sin modelo de lenguaje.
/// Cuando hace falta comprensión más fina, un asistente externo puede llamar a las
/// herramientas MCP con los filtros ya estructurados.
/// </summary>
public static class PromptParser
{
    private static readonly (string[] Words, RecognitionKind Kind)[] KindWords =
    {
        (new[] { "rostro", "rostros", "cara", "caras", "persona", "personas", "gente", "facial" }, RecognitionKind.Face),
        (new[] { "matricula", "matriculas", "patente", "patentes", "placa", "placas", "vehiculo", "vehiculos", "auto", "autos", "coche", "coches" }, RecognitionKind.Plate),
        (new[] { "objeto", "objetos", "animal", "animales", "perro", "perros", "gato", "gatos", "mochila", "maleta" }, RecognitionKind.Object),
        (new[] { "codigo", "codigos", "qr", "barras", "barcode" }, RecognitionKind.Code),
        (new[] { "texto", "escritura", "cartel", "carteles", "letrero", "letreros" }, RecognitionKind.Text),
        (new[] { "alerta", "alertas", "sospechoso", "sospechosa", "sospechosos", "sospechosas", "actividad", "actividades", "incidente", "incidentes" }, RecognitionKind.Activity),
    };

    /// <summary>Clases COCO frecuentes con su nombre en castellano.</summary>
    private static readonly Dictionary<string, string> SpanishClasses = new(StringComparer.Ordinal)
    {
        ["persona"] = "person", ["personas"] = "person", ["gente"] = "person",
        ["perro"] = "dog", ["perros"] = "dog",
        ["gato"] = "cat", ["gatos"] = "cat",
        ["pajaro"] = "bird", ["pajaros"] = "bird", ["ave"] = "bird", ["aves"] = "bird",
        ["caballo"] = "horse", ["caballos"] = "horse",
        ["vaca"] = "cow", ["vacas"] = "cow",
        ["oveja"] = "sheep", ["ovejas"] = "sheep",
        ["oso"] = "bear", ["osos"] = "bear",
        ["coche"] = "car", ["coches"] = "car", ["auto"] = "car", ["autos"] = "car", ["carro"] = "car",
        ["moto"] = "motorcycle", ["motos"] = "motorcycle", ["motocicleta"] = "motorcycle",
        ["camion"] = "truck", ["camiones"] = "truck",
        ["autobus"] = "bus", ["bus"] = "bus", ["omnibus"] = "bus",
        ["bicicleta"] = "bicycle", ["bici"] = "bicycle",
        ["mochila"] = "backpack", ["mochilas"] = "backpack",
        ["bolso"] = "handbag", ["cartera"] = "handbag",
        ["maleta"] = "suitcase", ["valija"] = "suitcase",
        ["cuchillo"] = "knife",
    };

    private static readonly string[] AnimalClasses =
        { "dog", "cat", "bird", "horse", "sheep", "cow", "bear", "elephant", "zebra", "giraffe" };

    public static SearchQuery Parse(string? prompt, DateTimeOffset now, IReadOnlyDictionary<string, Guid>? cameras = null)
    {
        var query = new SearchQuery { RawPrompt = prompt };
        if (string.IsNullOrWhiteSpace(prompt)) return query;

        var normalized = Fold(prompt);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var consumed = new HashSet<int>();

        ParseKind(words, query, consumed);
        ParseStatus(words, query, consumed);
        ParseObjectClass(words, query, consumed);
        ParseTime(words, query, consumed, now);
        ParseCamera(prompt, normalized, query, cameras);

        // Lo que no se ha interpretado como filtro se busca como texto literal.
        var leftovers = words.Where((_, i) => !consumed.Contains(i))
                             .Where(w => w.Length > 2 && !StopWords.Contains(w))
                             .ToList();

        if (leftovers.Count > 0)
            query.FreeText = string.Join(' ', leftovers);

        return query;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "que", "los", "las", "del", "para", "con", "por", "una", "unos", "unas", "sus",
        "muestrame", "muestra", "buscar", "busca", "busque", "dame", "quiero", "ver",
        "todos", "todas", "todo", "toda", "hay", "han", "hubo", "the", "and",
    };

    private static void ParseKind(string[] words, SearchQuery query, HashSet<int> consumed)
    {
        for (var i = 0; i < words.Length; i++)
        {
            foreach (var (candidates, kind) in KindWords)
            {
                if (!candidates.Contains(words[i])) continue;

                query.Kind ??= kind;
                if (kind == RecognitionKind.Activity) query.OnlyAlerts = true;
                consumed.Add(i);
                break;
            }
        }
    }

    private static void ParseStatus(string[] words, SearchQuery query, HashSet<int> consumed)
    {
        for (var i = 0; i < words.Length; i++)
        {
            switch (words[i])
            {
                case "desconocido" or "desconocida" or "desconocidos" or "desconocidas"
                     or "sinidentificar" or "extranos" or "extranas":
                    query.OnlyKnown = false;
                    consumed.Add(i);
                    break;

                case "conocido" or "conocida" or "conocidos" or "conocidas"
                     or "identificado" or "identificada" or "identificados" or "identificadas"
                     or "registrado" or "registrada" or "registrados" or "registradas":
                    query.OnlyKnown = true;
                    consumed.Add(i);
                    break;

                case "autorizado" or "autorizada" or "autorizados" or "autorizadas":
                    // "no autorizado" se detecta mirando la palabra anterior.
                    if (i > 0 && (words[i - 1] == "no" || words[i - 1] == "sin"))
                    {
                        query.OnlyUnauthorized = true;
                        consumed.Add(i - 1);
                    }
                    consumed.Add(i);
                    break;

                case "nuevo" or "nueva" or "nuevos" or "nuevas" or "pendiente" or "pendientes":
                    query.OnlyKnown = false;
                    consumed.Add(i);
                    break;
            }
        }
    }

    private static void ParseObjectClass(string[] words, SearchQuery query, HashSet<int> consumed)
    {
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i] is "animal" or "animales")
            {
                // "animales" no es una clase concreta: se resuelve más abajo con AnimalClasses.
                query.Kind ??= RecognitionKind.Object;
                query.ObjectClass = "@animal";
                consumed.Add(i);
                continue;
            }

            if (SpanishClasses.TryGetValue(words[i], out var cocoClass))
            {
                query.ObjectClass ??= cocoClass;
                consumed.Add(i);
            }
        }
    }

    private static void ParseTime(string[] words, SearchQuery query, HashSet<int> consumed, DateTimeOffset now)
    {
        var localToday = now.LocalDateTime.Date;

        for (var i = 0; i < words.Length; i++)
        {
            switch (words[i])
            {
                case "hoy":
                    query.FromUtc = localToday.ToUniversalTime();
                    consumed.Add(i);
                    break;

                case "ayer":
                    query.FromUtc = localToday.AddDays(-1).ToUniversalTime();
                    query.ToUtc = localToday.ToUniversalTime();
                    consumed.Add(i);
                    break;

                case "anoche":
                    query.FromUtc = localToday.AddDays(-1).AddHours(20).ToUniversalTime();
                    query.ToUtc = localToday.AddHours(7).ToUniversalTime();
                    consumed.Add(i);
                    break;

                case "semana":
                    query.FromUtc = localToday.AddDays(-7).ToUniversalTime();
                    consumed.Add(i);
                    MarkNeighbours(words, consumed, i, "esta", "ultima", "ultimos", "pasada");
                    break;

                case "mes":
                    query.FromUtc = localToday.AddMonths(-1).ToUniversalTime();
                    consumed.Add(i);
                    MarkNeighbours(words, consumed, i, "este", "ultimo", "pasado");
                    break;

                case "hora" or "horas":
                    // "última hora" / "últimas 3 horas"
                    var hours = 1;
                    if (i > 0 && int.TryParse(words[i - 1], out var parsedHours) && parsedHours is > 0 and <= 72)
                    {
                        hours = parsedHours;
                        consumed.Add(i - 1);
                    }
                    query.FromUtc = now.UtcDateTime.AddHours(-hours);
                    consumed.Add(i);
                    MarkNeighbours(words, consumed, i, "ultima", "ultimas", "ultimos", "ultimo");
                    break;

                case "minuto" or "minutos":
                    var minutes = 15;
                    if (i > 0 && int.TryParse(words[i - 1], out var parsedMinutes) && parsedMinutes is > 0 and <= 600)
                    {
                        minutes = parsedMinutes;
                        consumed.Add(i - 1);
                    }
                    query.FromUtc = now.UtcDateTime.AddMinutes(-minutes);
                    consumed.Add(i);
                    MarkNeighbours(words, consumed, i, "ultima", "ultimas", "ultimos", "ultimo");
                    break;

                case "dia" or "dias":
                    var days = 1;
                    if (i > 0 && int.TryParse(words[i - 1], out var parsedDays) && parsedDays is > 0 and <= 365)
                    {
                        days = parsedDays;
                        consumed.Add(i - 1);
                    }
                    query.FromUtc = localToday.AddDays(-days).ToUniversalTime();
                    consumed.Add(i);
                    MarkNeighbours(words, consumed, i, "ultimos", "ultimas", "ultimo", "ultima");
                    break;
            }

            // Fecha explícita dd/MM/yyyy o yyyy-MM-dd
            if (DateTime.TryParse(words[i], CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
                || DateTime.TryParse(words[i], CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                query.FromUtc = date.Date.ToUniversalTime();
                query.ToUtc = date.Date.AddDays(1).ToUniversalTime();
                consumed.Add(i);
            }
        }
    }

    private static void MarkNeighbours(string[] words, HashSet<int> consumed, int index, params string[] candidates)
    {
        if (index > 0 && candidates.Contains(words[index - 1])) consumed.Add(index - 1);
    }

    private static void ParseCamera(string original, string normalized, SearchQuery query,
                                    IReadOnlyDictionary<string, Guid>? cameras)
    {
        if (cameras is null || cameras.Count == 0) return;

        // Se busca el nombre de cámara más largo que aparezca en la frase, para que
        // "entrada principal" gane a "entrada".
        foreach (var (name, id) in cameras.OrderByDescending(kv => kv.Key.Length))
        {
            var folded = Fold(name);
            if (folded.Length >= 3 && normalized.Contains(folded, StringComparison.Ordinal))
            {
                query.CameraId = id;
                query.CameraName = name;
                return;
            }
        }
    }

    /// <summary>Minúsculas, sin acentos y sin signos: simplifica la comparación de palabras.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is '/' or '-' or ':') sb.Append(ch);
            else sb.Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>Clases consideradas animales, para la consulta genérica "animales".</summary>
    public static IReadOnlyList<string> AnimalClassNames => AnimalClasses;
}
