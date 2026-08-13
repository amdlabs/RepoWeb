using IVZVision.Core.Detection;
using IVZVision.Vision.Imaging;
using Xunit;

namespace IVZVision.Tests;

/// <summary>
/// El mapeo del cuadro de la red al fotograma original es donde más fácil se
/// cuelan errores de un píxel, así que se comprueba con números cerrados.
/// </summary>
public class LetterboxTests
{
    [Fact]
    public void Un_Fotograma_Panoramico_Se_Mapea_De_Vuelta_Sin_Error()
    {
        // 1280x720 → 640x640: escala 0,5, alto útil 360, relleno vertical 140.
        var transform = new LetterboxTransform(Scale: 0.5f, PadX: 0, PadY: 140,
                                               SourceWidth: 1280, SourceHeight: 720);

        var box = transform.ToSource(100, 180, 200, 100);

        Assert.Equal(200, box.X, 3);
        Assert.Equal(80, box.Y, 3);
        Assert.Equal(400, box.Width, 3);
        Assert.Equal(200, box.Height, 3);
    }

    [Fact]
    public void El_Centro_De_La_Imagen_De_Red_Cae_En_El_Centro_Del_Fotograma()
    {
        var transform = new LetterboxTransform(0.5f, 0, 140, 1280, 720);

        var (x, y) = transform.PointToSource(320, 320);

        Assert.Equal(640, x, 3);
        Assert.Equal(360, y, 3);
    }

    [Fact]
    public void Un_Fotograma_Cuadrado_No_Lleva_Relleno()
    {
        var transform = new LetterboxTransform(1f, 0, 0, 640, 640);

        var box = transform.ToSource(10, 20, 30, 40);

        Assert.Equal(new BoxF(10, 20, 30, 40), box);
    }

    [Fact]
    public void El_Mapeo_Nunca_Devuelve_Coordenadas_Fuera_Del_Fotograma()
    {
        var transform = new LetterboxTransform(0.5f, 0, 140, 1280, 720);

        // Un cuadro que invade la banda de relleno superior.
        var box = transform.ToSource(0, 0, 640, 200);

        Assert.True(box.X >= 0);
        Assert.True(box.Y >= 0);
        Assert.True(box.Right <= 1280);
        Assert.True(box.Bottom <= 720);
    }

    [Fact]
    public void La_Supresion_De_No_Maximos_Se_Queda_Con_La_Mejor_Deteccion()
    {
        var boxes = new[]
        {
            new BoxF(10, 10, 100, 100),   // 0 · puntuación media
            new BoxF(14, 14, 100, 100),   // 1 · casi igual que la 0, mejor puntuación
            new BoxF(400, 400, 80, 80),   // 2 · objeto distinto
        };
        var scores = new[] { 0.80f, 0.95f, 0.70f };

        var keep = ImageOps.NonMaxSuppression(boxes, scores, iouThreshold: 0.45f);

        Assert.Equal(2, keep.Count);
        Assert.Contains(1, keep);   // se conserva la de mayor puntuación
        Assert.Contains(2, keep);   // y el objeto separado
        Assert.DoesNotContain(0, keep);
    }

    [Fact]
    public void La_Supresion_No_Descarta_Objetos_Que_No_Se_Solapan()
    {
        var boxes = new[]
        {
            new BoxF(0, 0, 50, 50),
            new BoxF(100, 100, 50, 50),
            new BoxF(200, 200, 50, 50),
        };
        var scores = new[] { 0.9f, 0.8f, 0.7f };

        var keep = ImageOps.NonMaxSuppression(boxes, scores, iouThreshold: 0.45f);

        Assert.Equal(3, keep.Count);
    }
}
