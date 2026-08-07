using IVZVision.Core.Configuration;
using IVZVision.Core.Util;
using Xunit;

namespace IVZVision.Tests;

/// <summary>
/// Las matrículas de Uruguay son 3 letras y 4 números. Conocer el formato permite
/// dos cosas: descartar lecturas imposibles y corregir las confusiones del OCR.
/// </summary>
public class PlateFormatTests
{
    [Theory]
    [InlineData("SAB1234", true)]
    [InlineData("ABC0000", true)]
    [InlineData("AB1234", false)]     // sólo dos letras
    [InlineData("ABCD123", false)]    // cuatro letras
    [InlineData("1234ABC", false)]    // formato español
    [InlineData("ABC123", false)]     // tres números
    public void Uruguay_Son_Tres_Letras_Y_Cuatro_Numeros(string plate, bool expected)
    {
        Assert.Equal(expected, PlateText.IsValidForFormat(plate, PlateFormat.Uruguay, null));
    }

    [Theory]
    [InlineData("5AB1234", "SAB1234")]   // 5 leído donde va una letra → S
    [InlineData("A8C1234", "ABC1234")]   // 8 → B
    [InlineData("ABC12O4", "ABC1204")]   // O leída donde va un número → 0
    [InlineData("ABCI234", "ABC1234")]   // I → 1
    [InlineData("0BC1234", "OBC1234")]   // 0 → O
    public void Se_Corrigen_Las_Confusiones_Tipicas_Del_Ocr(string raw, string expected)
    {
        Assert.Equal(expected, PlateText.CoerceToFormat(raw, PlateFormat.Uruguay, null));
    }

    [Fact]
    public void Una_Lectura_Que_No_Encaja_En_El_Formato_Se_Deja_Como_Estaba()
    {
        // Longitud distinta: no hay forma segura de repararla, así que no se toca.
        Assert.Equal("ABC12", PlateText.CoerceToFormat("ABC12", PlateFormat.Uruguay, null));
    }

    [Fact]
    public void La_Correccion_No_Empeora_Una_Matricula_Ya_Correcta()
    {
        Assert.Equal("SAB1234", PlateText.CoerceToFormat("SAB1234", PlateFormat.Uruguay, null));
    }

    [Fact]
    public void El_Formato_Generico_Acepta_Cualquier_Combinacion_Razonable()
    {
        Assert.True(PlateText.IsValidForFormat("ABC1234", PlateFormat.Generic, null));
        Assert.True(PlateText.IsValidForFormat("1234ABC", PlateFormat.Generic, null));
    }

    [Theory]
    [InlineData(PlateFormat.Spain, "1234ABC", true)]
    [InlineData(PlateFormat.Spain, "ABC1234", false)]
    [InlineData(PlateFormat.Argentina, "AB123CD", true)]
    [InlineData(PlateFormat.Argentina, "ABC1234", false)]
    public void Otros_Paises_Tienen_Su_Propio_Patron(PlateFormat format, string plate, bool expected)
    {
        Assert.Equal(expected, PlateText.IsValidForFormat(plate, format, null));
    }

    [Fact]
    public void Un_Patron_Propio_Se_Aplica_Tal_Cual()
    {
        Assert.True(PlateText.IsValidForFormat("XY99", PlateFormat.Custom, "^[A-Z]{2}[0-9]{2}$"));
        Assert.False(PlateText.IsValidForFormat("XYZ99", PlateFormat.Custom, "^[A-Z]{2}[0-9]{2}$"));
    }

    [Fact]
    public void Un_Patron_Propio_Mal_Escrito_No_Bloquea_Las_Lecturas()
    {
        // Antes que rechazar todo por una expresión inválida, se deja pasar.
        Assert.True(PlateText.IsValidForFormat("ABC1234", PlateFormat.Custom, "["));
    }

    [Fact]
    public void La_Validacion_Completa_Combina_Longitud_Y_Formato()
    {
        Assert.True(PlateText.LooksValid("SAB1234", PlateFormat.Uruguay, null, 4, 10));
        Assert.False(PlateText.LooksValid("SAB123", PlateFormat.Uruguay, null, 4, 10));
    }

    [Theory]
    [InlineData(PlateFormat.Uruguay, "SAB1234", "SAB 1234")]
    [InlineData(PlateFormat.Spain, "1234ABC", "1234 ABC")]
    [InlineData(PlateFormat.Generic, "SAB1234", "SAB1234")]
    public void La_Presentacion_Agrupa_Segun_El_Pais(PlateFormat format, string plate, string expected)
    {
        Assert.Equal(expected, PlateText.Format(plate, format));
    }
}
