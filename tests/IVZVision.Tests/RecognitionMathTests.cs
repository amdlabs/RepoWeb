using IVZVision.Core.Util;
using Xunit;

namespace IVZVision.Tests;

public class RecognitionMathTests
{
    [Fact]
    public void Normalizar_Deja_El_Vector_Con_Norma_Uno()
    {
        var normalized = VectorMath.L2Normalize(new[] { 3f, 4f });

        Assert.Equal(0.6f, normalized[0], 5);
        Assert.Equal(0.8f, normalized[1], 5);
    }

    [Fact]
    public void Normalizar_Un_Vector_Nulo_No_Divide_Por_Cero()
    {
        var normalized = VectorMath.L2Normalize(new[] { 0f, 0f, 0f });

        Assert.All(normalized, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Similitud_Coseno_De_Vectores_Iguales_Es_Uno()
    {
        var v = new[] { 0.2f, -0.5f, 0.83f };
        Assert.Equal(1f, VectorMath.CosineSimilarity(v, v), 4);
    }

    [Fact]
    public void Similitud_Coseno_De_Vectores_Opuestos_Es_Menos_Uno()
    {
        var a = new[] { 1f, 2f, 3f };
        var b = new[] { -1f, -2f, -3f };
        Assert.Equal(-1f, VectorMath.CosineSimilarity(a, b), 4);
    }

    [Fact]
    public void Similitud_Coseno_De_Vectores_Perpendiculares_Es_Cero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity(new[] { 1f, 0f }, new[] { 0f, 1f }), 4);
    }

    [Fact]
    public void Similitud_Coseno_Con_Longitudes_Distintas_Devuelve_Menos_Uno()
    {
        Assert.Equal(-1f, VectorMath.CosineSimilarity(new[] { 1f, 0f }, new[] { 1f, 0f, 0f }));
    }

    [Fact]
    public void Serializar_Y_Deserializar_Un_Embedding_Conserva_Los_Valores()
    {
        var original = new[] { 0.123f, -0.456f, 0.789f, 1e-6f };

        var restored = VectorMath.FromBytes(VectorMath.ToBytes(original));

        Assert.Equal(original.Length, restored.Length);
        for (var i = 0; i < original.Length; i++)
            Assert.Equal(original[i], restored[i]);
    }

    [Fact]
    public void Deserializar_Bytes_Vacios_Devuelve_Vector_Vacio()
    {
        Assert.Empty(VectorMath.FromBytes(null));
        Assert.Empty(VectorMath.FromBytes(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("1234-abc", "1234ABC")]
    [InlineData(" 1234 ABC ", "1234ABC")]
    [InlineData("m-4521-jk", "M4521JK")]
    [InlineData("Ñ1234", "N1234")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Normalizar_Matricula_Quita_Separadores_Y_Acentos(string? raw, string expected)
    {
        Assert.Equal(expected, PlateText.Normalize(raw));
    }

    [Theory]
    [InlineData("1234ABC", true)]     // formato español actual
    [InlineData("M4521JK", true)]     // formato español antiguo
    [InlineData("ABC", false)]        // sin dígitos
    [InlineData("123", false)]        // demasiado corta
    [InlineData("12345678901", false)] // demasiado larga
    public void Validar_Matricula_Exige_Digitos_Y_Longitud(string plate, bool expected)
    {
        Assert.Equal(expected, PlateText.LooksValid(plate, minChars: 4, maxChars: 10));
    }
}
