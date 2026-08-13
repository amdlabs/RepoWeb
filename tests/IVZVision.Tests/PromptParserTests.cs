using IVZVision.Data.Entities;
using IVZVision.Data.Search;
using Xunit;

namespace IVZVision.Tests;

/// <summary>
/// El buscador acepta frases en castellano. El analizador es determinista: no
/// depende de ningún modelo de lenguaje, así que funciona sin conexión.
/// </summary>
public class PromptParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 15, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("rostros de hoy", RecognitionKind.Face)]
    [InlineData("caras desconocidas", RecognitionKind.Face)]
    [InlineData("matriculas de la entrada", RecognitionKind.Plate)]
    [InlineData("patentes registradas", RecognitionKind.Plate)]
    [InlineData("codigos QR leidos", RecognitionKind.Code)]
    [InlineData("alertas de anoche", RecognitionKind.Activity)]
    public void Se_Reconoce_El_Tipo_De_Sujeto(string prompt, RecognitionKind expected)
    {
        Assert.Equal(expected, PromptParser.Parse(prompt, Now).Kind);
    }

    [Fact]
    public void Desconocido_Filtra_Los_No_Identificados()
    {
        var query = PromptParser.Parse("personas desconocidas", Now);
        Assert.False(query.OnlyKnown);
    }

    [Fact]
    public void Identificado_Filtra_Los_Reconocidos()
    {
        var query = PromptParser.Parse("rostros identificados", Now);
        Assert.True(query.OnlyKnown);
    }

    [Fact]
    public void No_Autorizado_Se_Detecta_Con_La_Negacion_Delante()
    {
        Assert.True(PromptParser.Parse("personas no autorizadas", Now).OnlyUnauthorized);
        Assert.True(PromptParser.Parse("vehiculos sin autorizacion", Now).OnlyUnauthorized
                    || PromptParser.Parse("vehiculos sin autorizar", Now).FreeText is not null);
    }

    [Fact]
    public void Hoy_Empieza_A_Medianoche()
    {
        var query = PromptParser.Parse("eventos de hoy", Now);

        Assert.NotNull(query.FromUtc);
        Assert.Equal(Now.LocalDateTime.Date.ToUniversalTime(), query.FromUtc);
        Assert.Null(query.ToUtc);
    }

    [Fact]
    public void Ayer_Es_Un_Dia_Cerrado()
    {
        var query = PromptParser.Parse("matriculas de ayer", Now);

        Assert.NotNull(query.FromUtc);
        Assert.NotNull(query.ToUtc);
        Assert.Equal(TimeSpan.FromDays(1), query.ToUtc!.Value - query.FromUtc!.Value);
    }

    [Fact]
    public void Ultimas_Horas_Cuenta_Hacia_Atras_Desde_Ahora()
    {
        var query = PromptParser.Parse("rostros de las ultimas 3 horas", Now);

        Assert.NotNull(query.FromUtc);
        Assert.Equal(Now.UtcDateTime.AddHours(-3), query.FromUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Anoche_Cubre_La_Franja_Nocturna()
    {
        var query = PromptParser.Parse("alertas de anoche", Now);

        Assert.NotNull(query.FromUtc);
        Assert.NotNull(query.ToUtc);
        Assert.True(query.ToUtc > query.FromUtc);
    }

    [Theory]
    [InlineData("perros en el patio", "dog")]
    [InlineData("gatos de hoy", "cat")]
    [InlineData("coches desconocidos", "car")]
    [InlineData("mochilas", "backpack")]
    public void Las_Clases_Se_Traducen_Del_Castellano(string prompt, string expected)
    {
        Assert.Equal(expected, PromptParser.Parse(prompt, Now).ObjectClass);
    }

    [Fact]
    public void Animales_Es_Un_Grupo_De_Clases()
    {
        var query = PromptParser.Parse("animales de esta semana", Now);

        Assert.Equal("@animal", query.ObjectClass);
        Assert.Contains("dog", PromptParser.AnimalClassNames);
        Assert.Contains("cat", PromptParser.AnimalClassNames);
    }

    [Fact]
    public void El_Nombre_De_La_Camara_Se_Resuelve_A_Su_Identificador()
    {
        var id = Guid.NewGuid();
        var cameras = new Dictionary<string, Guid> { ["Entrada Principal"] = id, ["Patio"] = Guid.NewGuid() };

        var query = PromptParser.Parse("personas en la entrada principal", Now, cameras);

        Assert.Equal(id, query.CameraId);
        Assert.Equal("Entrada Principal", query.CameraName);
    }

    [Fact]
    public void Gana_El_Nombre_De_Camara_Mas_Largo()
    {
        var general = Guid.NewGuid();
        var specific = Guid.NewGuid();
        var cameras = new Dictionary<string, Guid> { ["Entrada"] = general, ["Entrada Principal"] = specific };

        Assert.Equal(specific, PromptParser.Parse("rostros en entrada principal", Now, cameras).CameraId);
    }

    [Fact]
    public void Lo_No_Interpretado_Queda_Como_Busqueda_De_Texto()
    {
        var query = PromptParser.Parse("matriculas SAB1234", Now);

        Assert.Equal(RecognitionKind.Plate, query.Kind);
        Assert.Contains("sab1234", query.FreeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Las_Muletillas_No_Ensucian_La_Busqueda_De_Texto()
    {
        var query = PromptParser.Parse("muestrame todas las personas desconocidas", Now);

        Assert.Null(query.FreeText);
        Assert.False(query.OnlyKnown);
    }

    [Fact]
    public void Una_Consulta_Vacia_No_Filtra_Nada()
    {
        var query = PromptParser.Parse("", Now);

        Assert.Null(query.Kind);
        Assert.Null(query.FromUtc);
        Assert.Null(query.FreeText);
        Assert.Equal("todo el histórico", query.Describe());
    }

    [Fact]
    public void La_Interpretacion_Es_Legible_Para_El_Usuario()
    {
        var query = PromptParser.Parse("personas desconocidas de hoy", Now);
        var description = query.Describe();

        Assert.Contains("rostros", description);
        Assert.Contains("desconocidos", description);
    }
}
