using IVZVision.Core.Detection;
using Xunit;

namespace IVZVision.Tests;

public class GeometryTests
{
    [Fact]
    public void IoU_De_Cuadros_Identicos_Es_Uno()
    {
        var box = new BoxF(10, 10, 40, 40);
        Assert.Equal(1f, BoxF.IntersectionOverUnion(box, box), 4);
    }

    [Fact]
    public void IoU_De_Cuadros_Disjuntos_Es_Cero()
    {
        var a = new BoxF(0, 0, 10, 10);
        var b = new BoxF(50, 50, 10, 10);
        Assert.Equal(0f, BoxF.IntersectionOverUnion(a, b));
    }

    [Fact]
    public void IoU_De_Solape_Parcial_Es_El_Esperado()
    {
        // Dos cuadros de 10x10 desplazados 5 px: intersección 25, unión 175.
        var a = new BoxF(0, 0, 10, 10);
        var b = new BoxF(5, 5, 10, 10);
        Assert.Equal(25f / 175f, BoxF.IntersectionOverUnion(a, b), 4);
    }

    [Fact]
    public void ClampTo_Recorta_Al_Fotograma()
    {
        var box = new BoxF(-20, -10, 100, 100).ClampTo(80, 60);

        Assert.Equal(0, box.X);
        Assert.Equal(0, box.Y);
        Assert.Equal(80, box.Width);
        Assert.Equal(60, box.Height);
    }

    [Fact]
    public void Expand_Amplia_Y_Respeta_Los_Limites()
    {
        var box = new BoxF(50, 50, 100, 100).Expand(0.10f, 640, 480);

        Assert.Equal(40, box.X, 3);
        Assert.Equal(40, box.Y, 3);
        Assert.Equal(120, box.Width, 3);
        Assert.Equal(120, box.Height, 3);

        // Pegado al borde no se sale del fotograma.
        var edge = new BoxF(600, 440, 40, 40).Expand(0.5f, 640, 480);
        Assert.True(edge.Right <= 640);
        Assert.True(edge.Bottom <= 480);
    }
}
