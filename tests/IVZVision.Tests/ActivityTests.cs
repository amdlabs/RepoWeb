using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Activity;
using Xunit;

namespace IVZVision.Tests;

public class ObjectTrackerTests
{
    private static readonly ActivityConfig Config = new() { TrackIouThreshold = 0.3f, TrackMaxMissedFrames = 3 };

    [Fact]
    public void Un_Objeto_Que_Se_Mueve_Poco_Conserva_Su_Identificador()
    {
        var tracker = new ObjectTracker();
        var now = DateTimeOffset.Now;

        var first = tracker.Update(new[] { (new BoxF(100, 100, 60, 120), 0.9f, "person") }, now, Config);
        var id = first[0].Id;

        var second = tracker.Update(new[] { (new BoxF(108, 104, 60, 120), 0.9f, "person") },
                                    now.AddMilliseconds(200), Config);

        Assert.Equal(id, second[0].Id);
    }

    [Fact]
    public void Un_Salto_Grande_Se_Trata_Como_Un_Objeto_Nuevo()
    {
        var tracker = new ObjectTracker();
        var now = DateTimeOffset.Now;

        var first = tracker.Update(new[] { (new BoxF(0, 0, 50, 50), 0.9f, "person") }, now, Config);
        var second = tracker.Update(new[] { (new BoxF(600, 400, 50, 50), 0.9f, "person") },
                                    now.AddMilliseconds(200), Config);

        Assert.NotEqual(first[0].Id, second[0].Id);
    }

    [Fact]
    public void Dos_Clases_Distintas_Nunca_Se_Confunden()
    {
        var tracker = new ObjectTracker();
        var now = DateTimeOffset.Now;

        var person = tracker.Update(new[] { (new BoxF(100, 100, 60, 120), 0.9f, "person") }, now, Config);
        // Misma posición pero es un perro: no puede heredar el seguimiento de la persona.
        var dog = tracker.Update(new[] { (new BoxF(100, 100, 60, 120), 0.9f, "dog") },
                                 now.AddMilliseconds(200), Config);

        Assert.NotEqual(person[0].Id, dog[0].Id);
    }

    [Fact]
    public void Un_Objeto_Que_Desaparece_Se_Descarta_Tras_Varios_Fotogramas()
    {
        var tracker = new ObjectTracker();
        var now = DateTimeOffset.Now;

        tracker.Update(new[] { (new BoxF(10, 10, 40, 40), 0.9f, "person") }, now, Config);
        Assert.Single(tracker.Tracks);

        for (var i = 1; i <= Config.TrackMaxMissedFrames + 1; i++)
            tracker.Update(Array.Empty<(BoxF, float, string)>(), now.AddSeconds(i), Config);

        Assert.Empty(tracker.Tracks);
    }

    [Fact]
    public void La_Velocidad_Se_Calcula_Sobre_El_Recorrido_Reciente()
    {
        var tracker = new ObjectTracker();
        var now = DateTimeOffset.Now;

        // Cuadros de 100x100 desplazados 30 px: solapan lo suficiente (IoU 0,54)
        // para que el seguimiento los una y se pueda medir la velocidad.
        tracker.Update(new[] { (new BoxF(0, 0, 100, 100), 0.9f, "person") }, now, Config);
        var moved = tracker.Update(new[] { (new BoxF(30, 0, 100, 100), 0.9f, "person") }, now.AddSeconds(1), Config);

        Assert.Single(moved);
        Assert.Equal(30, moved[0].SpeedPixelsPerSecond(), 0);
    }

    [Fact]
    public void Un_Objeto_Recien_Visto_No_Tiene_Velocidad()
    {
        var tracker = new ObjectTracker();

        var tracks = tracker.Update(new[] { (new BoxF(0, 0, 40, 40), 0.9f, "person") }, DateTimeOffset.Now, Config);

        // Con una sola posición no hay recorrido del que deducir velocidad.
        Assert.Equal(0, tracks[0].SpeedPixelsPerSecond());
    }
}

public class ActivityAnalyzerTests
{
    private static ActivityConfig Rules() => new()
    {
        LoiteringEnabled = false,
        IntrusionEnabled = false,
        CrowdEnabled = false,
        RunningEnabled = false,
        ScheduleEnabled = false,
        AnimalEnabled = false,
        CoveredFaceEnabled = false,
        AlertCooldownSeconds = 60,
    };

    private static IReadOnlyList<Track> TrackFor(string className, BoxF box, DateTimeOffset firstSeen,
                                                 DateTimeOffset now, ActivityConfig config)
    {
        var tracker = new ObjectTracker();
        tracker.Update(new[] { (box, 0.9f, className) }, firstSeen, config);
        return tracker.Update(new[] { (box, 0.9f, className) }, now, config);
    }

    [Fact]
    public void Merodeo_Salta_Cuando_Se_Supera_La_Permanencia()
    {
        var config = Rules();
        config.LoiteringEnabled = true;
        config.LoiteringSeconds = 10;

        var start = DateTimeOffset.Now;
        var tracks = TrackFor("person", new BoxF(10, 10, 50, 100), start, start.AddSeconds(15), config);

        var alerts = new ActivityAnalyzer().Evaluate(tracks, Array.Empty<BoxF>(), null, 1280,
                                                     start.AddSeconds(15), config);

        var alert = Assert.Single(alerts);
        Assert.Equal(ActivityKind.Loitering, alert.Kind);
        Assert.Contains("15", alert.Explanation);
    }

    [Fact]
    public void Merodeo_No_Salta_Antes_De_Tiempo()
    {
        var config = Rules();
        config.LoiteringEnabled = true;
        config.LoiteringSeconds = 30;

        var start = DateTimeOffset.Now;
        var tracks = TrackFor("person", new BoxF(10, 10, 50, 100), start, start.AddSeconds(5), config);

        var alerts = new ActivityAnalyzer().Evaluate(tracks, Array.Empty<BoxF>(), null, 1280,
                                                     start.AddSeconds(5), config);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Intrusion_Salta_Solo_Dentro_De_La_Zona()
    {
        var config = Rules();
        config.IntrusionEnabled = true;

        var zone = new BoxF(200, 200, 400, 400);
        var now = DateTimeOffset.Now;

        var inside = TrackFor("person", new BoxF(300, 300, 60, 120), now, now, config);
        var insideAlerts = new ActivityAnalyzer().Evaluate(inside, Array.Empty<BoxF>(), zone, 1280, now, config);
        Assert.Equal(ActivityKind.Intrusion, Assert.Single(insideAlerts).Kind);

        var outside = TrackFor("person", new BoxF(900, 50, 60, 120), now, now, config);
        var outsideAlerts = new ActivityAnalyzer().Evaluate(outside, Array.Empty<BoxF>(), zone, 1280, now, config);
        Assert.Empty(outsideAlerts);
    }

    [Fact]
    public void Aglomeracion_Cuenta_Solo_Personas()
    {
        var config = Rules();
        config.CrowdEnabled = true;
        config.CrowdMinPeople = 3;

        var now = DateTimeOffset.Now;
        var tracker = new ObjectTracker();

        var detections = new List<(BoxF, float, string)>
        {
            (new BoxF(0, 0, 40, 80), 0.9f, "person"),
            (new BoxF(100, 0, 40, 80), 0.9f, "person"),
            (new BoxF(200, 0, 40, 80), 0.9f, "person"),
            (new BoxF(300, 0, 40, 80), 0.9f, "car"),
        };

        var tracks = tracker.Update(detections, now, config);
        var alerts = new ActivityAnalyzer().Evaluate(tracks, Array.Empty<BoxF>(), null, 1280, now, config);

        var alert = Assert.Single(alerts);
        Assert.Equal(ActivityKind.Crowd, alert.Kind);
        Assert.Contains("3 personas", alert.Explanation);
    }

    [Fact]
    public void Rostro_Oculto_Salta_Solo_Si_Nunca_Se_Vio_La_Cara()
    {
        var config = Rules();
        config.CoveredFaceEnabled = true;
        config.CoveredFaceSeconds = 5;

        var start = DateTimeOffset.Now;
        var personBox = new BoxF(100, 100, 100, 200);
        var now = start.AddSeconds(10);

        // Sin ningún rostro dentro del cuadro de la persona.
        var hidden = TrackFor("person", personBox, start, now, config);
        var alerts = new ActivityAnalyzer().Evaluate(hidden, Array.Empty<BoxF>(), null, 1280, now, config);
        Assert.Equal(ActivityKind.CoveredFace, Assert.Single(alerts).Kind);

        // Con un rostro dentro del cuadro, no hay alerta.
        var visible = TrackFor("person", personBox, start, now, config);
        var face = new[] { new BoxF(130, 110, 40, 40) };
        var noAlerts = new ActivityAnalyzer().Evaluate(visible, face, null, 1280, now, config);
        Assert.Empty(noAlerts);
    }

    [Fact]
    public void La_Misma_Alerta_No_Se_Repite_Dentro_Del_Tiempo_De_Guarda()
    {
        var config = Rules();
        config.LoiteringEnabled = true;
        config.LoiteringSeconds = 5;
        config.AlertCooldownSeconds = 300;

        var analyzer = new ActivityAnalyzer();
        var start = DateTimeOffset.Now;
        var tracker = new ObjectTracker();

        tracker.Update(new[] { (new BoxF(10, 10, 50, 100), 0.9f, "person") }, start, config);

        var first = tracker.Update(new[] { (new BoxF(10, 10, 50, 100), 0.9f, "person") }, start.AddSeconds(10), config);
        Assert.Single(analyzer.Evaluate(first, Array.Empty<BoxF>(), null, 1280, start.AddSeconds(10), config));

        var second = tracker.Update(new[] { (new BoxF(10, 10, 50, 100), 0.9f, "person") }, start.AddSeconds(12), config);
        Assert.Empty(analyzer.Evaluate(second, Array.Empty<BoxF>(), null, 1280, start.AddSeconds(12), config));
    }

    [Fact]
    public void La_Zona_Restringida_Se_Convierte_De_Porcentaje_A_Pixeles()
    {
        var camera = new CameraConfig
        {
            RestrictedZoneEnabled = true,
            RestrictedXPercent = 25,
            RestrictedYPercent = 50,
            RestrictedWidthPercent = 50,
            RestrictedHeightPercent = 25,
        };

        var zone = ActivityAnalyzer.ResolveRestrictedZone(camera, 1000, 800);

        Assert.NotNull(zone);
        Assert.Equal(250, zone!.Value.X, 1);
        Assert.Equal(400, zone.Value.Y, 1);
        Assert.Equal(500, zone.Value.Width, 1);
        Assert.Equal(200, zone.Value.Height, 1);
    }

    [Fact]
    public void Sin_Zona_Configurada_No_Hay_Zona_Restringida()
    {
        Assert.Null(ActivityAnalyzer.ResolveRestrictedZone(new CameraConfig(), 1000, 800));
    }
}
