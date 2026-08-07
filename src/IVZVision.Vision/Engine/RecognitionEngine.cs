using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Core.Util;
using IVZVision.Data;
using IVZVision.Vision.Codes;
using IVZVision.Vision.Faces;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Objects;
using IVZVision.Vision.Text;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace IVZVision.Vision.Engine;

/// <summary>Detección individual con su identidad ya resuelta, en coordenadas del fotograma analizado.</summary>
public sealed class AnalysisItem
{
    public ObservationKind Kind { get; init; }
    public BoxF Box { get; init; }
    public float Score { get; init; }
    public string? PlateText { get; init; }
    public float? OcrConfidence { get; init; }
    public string? ObjectClass { get; init; }
    public string? CodeValue { get; init; }
    public string? CodeFormat { get; init; }
    public string? TextValue { get; init; }

    /// <summary>Vector de características, para poder aprender el sujeto si se le pone nombre.</summary>
    public float[]? Embedding { get; init; }

    public IdentityMatch Match { get; init; } = IdentityMatch.Unknown;
}

/// <summary>Estado de carga de cada bloque de modelos, para la pantalla de configuración.</summary>
public sealed class ModelStatus
{
    public bool FaceDetectorReady { get; set; }
    public bool FaceEmbedderReady { get; set; }
    public bool PlateDetectorReady { get; set; }
    public bool PlateOcrReady { get; set; }
    public bool ObjectDetectorReady { get; set; }
    public bool ObjectEmbedderReady { get; set; }
    public bool TextDetectorReady { get; set; }
    public bool TextRecognizerReady { get; set; }

    public string? FaceError { get; set; }
    public string? PlateError { get; set; }
    public string? ObjectError { get; set; }
    public string? ObjectEmbedderError { get; set; }
    public string? TextError { get; set; }

    public bool FacesAvailable => FaceDetectorReady && FaceEmbedderReady;
    public bool PlatesAvailable => PlateDetectorReady && PlateOcrReady;
    public bool ObjectsAvailable => ObjectDetectorReady;
    public bool TextAvailable => TextDetectorReady && TextRecognizerReady;

    /// <summary>Los códigos no necesitan modelo: se leen con una biblioteca gestionada.</summary>
    public bool CodesAvailable => true;

    public string? FaceModelId { get; set; }
    public string? ObjectModelId { get; set; }
    public IReadOnlyList<string> ObjectClasses { get; set; } = Array.Empty<string>();
}

public sealed record FaceEnrollment(bool Success, string Message, float[]? Embedding = null,
                                    byte[]? AlignedJpeg = null, float Score = 0, string? ModelId = null);

/// <summary>
/// Punto único de acceso a los modelos locales. Carga perezosa, recarga sin
/// reiniciar la aplicación cuando cambia la configuración y degradación controlada:
/// cada bloque (rostros, matrículas, objetos, texto) funciona por su cuenta, de modo
/// que si falta uno el resto sigue operativo.
/// </summary>
public sealed class RecognitionEngine : IDisposable
{
    private readonly IConfigStore _config;
    private readonly KnownSubjectsIndex _index;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RecognitionEngine> _logger;
    private readonly string _contentRoot;
    private readonly object _gate = new();

    private YuNetFaceDetector? _faceDetector;
    private SFaceEmbedder? _faceEmbedder;
    private YoloDetector? _plateDetector;
    private CtcTextRecognizer? _plateOcr;
    private YoloDetector? _objectDetector;
    private ObjectEmbedder? _objectEmbedder;
    private DbTextDetector? _textDetector;
    private CtcTextRecognizer? _textRecognizer;
    private CodeReader? _codeReader;

    private bool _loaded;
    private bool _disposed;

    public RecognitionEngine(IConfigStore config, KnownSubjectsIndex index,
                             ILoggerFactory loggerFactory, string contentRoot)
    {
        _config = config;
        _index = index;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RecognitionEngine>();
        _contentRoot = contentRoot;

        _config.Changed += (_, _) => Reload();
    }

    public ModelStatus Status { get; private set; } = new();

    /// <summary>Descarga los modelos; se volverán a cargar en el siguiente análisis.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            DisposeModels();
            _loaded = false;
            _logger.LogInformation("Modelos marcados para recarga");
        }
    }

    /// <summary>Fuerza la carga inmediata y devuelve el estado resultante.</summary>
    public ModelStatus EnsureLoaded()
    {
        lock (_gate)
        {
            if (!_loaded) Load();
            return Status;
        }
    }

    public IReadOnlyList<AnalysisItem> Analyze(Mat analysisFrame, CameraConfig camera)
    {
        EnsureLoaded();

        var rec = _config.Current.Recognition;
        var items = new List<AnalysisItem>();

        if (camera.EnableFaceRecognition && Status.FacesAvailable)
            AnalyzeFaces(analysisFrame, rec, items);

        if (camera.EnablePlateRecognition && Status.PlatesAvailable)
            AnalyzePlates(analysisFrame, rec, items);

        if (camera.EnableObjectDetection && Status.ObjectsAvailable)
            AnalyzeObjects(analysisFrame, rec, items);

        if (camera.EnableCodeReading)
            AnalyzeCodes(analysisFrame, rec, items);

        if (camera.EnableTextReading && Status.TextAvailable)
            AnalyzeText(analysisFrame, rec, items);

        return items;
    }

    // ---- Rostros -----------------------------------------------------------

    private void AnalyzeFaces(Mat frame, RecognitionConfig rec, List<AnalysisItem> items)
    {
        YuNetFaceDetector? detector;
        SFaceEmbedder? embedder;
        lock (_gate) { detector = _faceDetector; embedder = _faceEmbedder; }
        if (detector is null || embedder is null) return;

        try
        {
            var faces = detector.Detect(frame, rec.FaceDetectionThreshold, rec.FaceNmsThreshold, rec.MinFaceSize);

            foreach (var face in faces)
            {
                IdentityMatch match;
                float[]? embedding = null;

                try
                {
                    embedding = embedder.Embed(frame, face.Landmarks);
                    match = _index.MatchFace(embedding, rec.FaceMatchThreshold);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo calcular el embedding de un rostro");
                    match = IdentityMatch.Unknown;
                }

                items.Add(new AnalysisItem
                {
                    Kind = ObservationKind.Face,
                    Box = face.Box,
                    Score = face.Score,
                    Embedding = embedding,
                    Match = match,
                });
            }
        }
        catch (Exception ex)
        {
            Disable(ex, "rostros", s => { s.FaceDetectorReady = false; s.FaceError = ex.Message; });
        }
    }

    // ---- Matrículas ---------------------------------------------------------

    private void AnalyzePlates(Mat frame, RecognitionConfig rec, List<AnalysisItem> items)
    {
        YoloDetector? detector;
        CtcTextRecognizer? ocr;
        lock (_gate) { detector = _plateDetector; ocr = _plateOcr; }
        if (detector is null || ocr is null) return;

        try
        {
            var plates = detector.Detect(frame, rec.PlateDetectionThreshold, rec.PlateNmsThreshold);

            foreach (var plate in plates)
            {
                // Un pequeño margen alrededor mejora bastante la lectura del OCR.
                using var crop = ImageOps.SafeCrop(frame, plate.Box.Expand(0.06f, frame.Width, frame.Height));
                if (crop is null) continue;

                TextReading reading;
                try
                {
                    reading = ocr.Read(crop);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo leer el texto de una matrícula");
                    continue;
                }

                var normalized = PlateText.Normalize(reading.Text);

                // Con un formato de país conocido se corrigen las confusiones del OCR
                // (0/O, 1/I, 5/S…) según qué posiciones son letras y cuáles números.
                if (rec.PlateFixOcrConfusions)
                    normalized = PlateText.CoerceToFormat(normalized, rec.PlateFormat, rec.PlateCustomPattern);

                var valid = reading.Confidence >= rec.PlateOcrMinConfidence
                            && PlateText.LooksValid(normalized, rec.PlateFormat, rec.PlateCustomPattern,
                                                    rec.PlateMinCharacters, rec.PlateMaxCharacters);

                var match = valid ? _index.MatchPlate(normalized) : IdentityMatch.Unknown;

                items.Add(new AnalysisItem
                {
                    Kind = ObservationKind.Plate,
                    Box = plate.Box,
                    Score = plate.Score,
                    PlateText = valid ? normalized : null,
                    OcrConfidence = reading.Confidence,
                    Match = match,
                });
            }
        }
        catch (Exception ex)
        {
            Disable(ex, "matrículas", s => { s.PlateDetectorReady = false; s.PlateError = ex.Message; });
        }
    }

    // ---- Objetos -------------------------------------------------------------

    private void AnalyzeObjects(Mat frame, RecognitionConfig rec, List<AnalysisItem> items)
    {
        YoloDetector? detector;
        ObjectEmbedder? embedder;
        lock (_gate) { detector = _objectDetector; embedder = _objectEmbedder; }
        if (detector is null) return;

        try
        {
            var detections = detector.Detect(frame, rec.ObjectDetectionThreshold, rec.ObjectNmsThreshold);

            var wanted = rec.ObjectClassesOfInterest.Count == 0
                ? null
                : new HashSet<string>(rec.ObjectClassesOfInterest, StringComparer.OrdinalIgnoreCase);

            foreach (var detection in detections)
            {
                if (wanted is not null && !wanted.Contains(detection.ClassName)) continue;

                float[]? embedding = null;
                var match = IdentityMatch.Unknown;

                if (embedder is not null)
                {
                    try
                    {
                        using var crop = ImageOps.SafeCrop(frame, detection.Box);
                        if (crop is not null)
                        {
                            embedding = embedder.Embed(crop);
                            match = _index.MatchObject(embedding, detection.ClassName, rec.ObjectMatchThreshold);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "No se pudo calcular el vector de un objeto");
                    }
                }

                items.Add(new AnalysisItem
                {
                    Kind = ObservationKind.Object,
                    Box = detection.Box,
                    Score = detection.Score,
                    ObjectClass = detection.ClassName,
                    Embedding = embedding,
                    Match = match,
                });
            }
        }
        catch (Exception ex)
        {
            Disable(ex, "objetos", s => { s.ObjectDetectorReady = false; s.ObjectError = ex.Message; });
        }
    }

    // ---- Códigos --------------------------------------------------------------

    private void AnalyzeCodes(Mat frame, RecognitionConfig rec, List<AnalysisItem> items)
    {
        CodeReader? reader;
        lock (_gate) { reader = _codeReader; }
        if (reader is null) return;

        foreach (var code in reader.Read(frame, rec.CodeMinLength))
        {
            items.Add(new AnalysisItem
            {
                Kind = ObservationKind.Code,
                Box = code.Box,
                Score = 1f,
                CodeValue = code.Value,
                CodeFormat = code.Format,
                Match = IdentityMatch.Unknown,
            });
        }
    }

    // ---- Texto ----------------------------------------------------------------

    private void AnalyzeText(Mat frame, RecognitionConfig rec, List<AnalysisItem> items)
    {
        DbTextDetector? detector;
        CtcTextRecognizer? recognizer;
        lock (_gate) { detector = _textDetector; recognizer = _textRecognizer; }
        if (detector is null || recognizer is null) return;

        try
        {
            foreach (var region in detector.Detect(frame, rec.TextDetectionThreshold).Take(40))
            {
                using var crop = DbTextDetector.CropUpright(frame, region);
                if (crop is null) continue;

                TextReading reading;
                try
                {
                    reading = recognizer.Read(crop);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "No se pudo reconocer una línea de texto");
                    continue;
                }

                var text = reading.Text.Trim();
                if (text.Length < rec.TextMinLength) continue;
                if (reading.Confidence < rec.TextMinConfidence) continue;

                items.Add(new AnalysisItem
                {
                    Kind = ObservationKind.Text,
                    Box = region.Box,
                    Score = region.Score,
                    TextValue = text,
                    OcrConfidence = reading.Confidence,
                    Match = IdentityMatch.Unknown,
                });
            }
        }
        catch (Exception ex)
        {
            Disable(ex, "texto", s => { s.TextDetectorReady = false; s.TextError = ex.Message; });
        }
    }

    // ---- Altas y búsqueda por imagen --------------------------------------------

    /// <summary>Extrae el embedding del rostro más grande de una foto, para dar de alta a una persona.</summary>
    public FaceEnrollment EnrollFace(Mat imageBgr)
    {
        EnsureLoaded();

        YuNetFaceDetector? detector;
        SFaceEmbedder? embedder;
        lock (_gate) { detector = _faceDetector; embedder = _faceEmbedder; }

        if (detector is null || embedder is null)
            return new FaceEnrollment(false,
                Status.FaceError ?? "Los modelos de reconocimiento facial no están cargados. Revise la configuración.");

        var rec = _config.Current.Recognition;

        try
        {
            // En una foto de alta se baja el listón del detector: la imagen es de estudio.
            var threshold = Math.Min(rec.FaceDetectionThreshold, 0.6f);
            var faces = detector.Detect(imageBgr, threshold, rec.FaceNmsThreshold, minFaceSize: 24);

            if (faces.Count == 0)
                return new FaceEnrollment(false, "No se ha detectado ningún rostro en la imagen.");

            var best = faces.OrderByDescending(f => f.Box.Area).First();

            using var aligned = FaceAligner.Align(imageBgr, best.Landmarks);
            var embedding = embedder.EmbedAligned(aligned);
            var jpeg = ImageOps.EncodeJpeg(aligned, 90);

            var message = faces.Count > 1
                ? $"Se han encontrado {faces.Count} rostros; se ha usado el más grande."
                : "Rostro extraído correctamente.";

            return new FaceEnrollment(true, message, embedding, jpeg, best.Score, embedder.ModelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al extraer el rostro de la imagen de alta");
            return new FaceEnrollment(false, $"Error al procesar la imagen: {ex.Message}");
        }
    }

    /// <summary>Vector de una imagen de ejemplo, para buscar objetos parecidos.</summary>
    public float[]? EmbedObject(Mat imageBgr)
    {
        EnsureLoaded();

        ObjectEmbedder? embedder;
        lock (_gate) { embedder = _objectEmbedder; }
        if (embedder is null) return null;

        try
        {
            return embedder.Embed(imageBgr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo calcular el vector de la imagen de ejemplo");
            return null;
        }
    }

    // ---- Carga ---------------------------------------------------------------

    private void Load()
    {
        var models = _config.Current.Models;
        var status = new ModelStatus();

        _codeReader = new CodeReader(_loggerFactory.CreateLogger<CodeReader>());

        TryLoad("rostros", () =>
        {
            _faceDetector = new YuNetFaceDetector(
                models.Resolve(models.FaceDetectorPath, _contentRoot), models,
                _loggerFactory.CreateLogger<YuNetFaceDetector>());
            status.FaceDetectorReady = true;

            _faceEmbedder = new SFaceEmbedder(
                models.Resolve(models.FaceEmbedderPath, _contentRoot), models,
                _loggerFactory.CreateLogger<SFaceEmbedder>());
            status.FaceEmbedderReady = true;
            status.FaceModelId = _faceEmbedder.ModelId;
        }, ex => status.FaceError = ex.Message);

        TryLoad("matrículas", () =>
        {
            _plateDetector = new YoloDetector(
                models.Resolve(models.PlateDetectorPath, _contentRoot), models.PlateDetectorInputSize,
                classNamesPath: null, models, _loggerFactory.CreateLogger<YoloDetector>());
            status.PlateDetectorReady = true;

            _plateOcr = new CtcTextRecognizer(
                models.Resolve(models.PlateOcrPath, _contentRoot),
                models.Resolve(models.PlateOcrCharsetPath, _contentRoot),
                CtcOptions.ForPlates(models), models, _loggerFactory.CreateLogger<CtcTextRecognizer>());
            status.PlateOcrReady = true;
        }, ex => status.PlateError = ex.Message);

        TryLoad("objetos", () =>
        {
            _objectDetector = new YoloDetector(
                models.Resolve(models.ObjectDetectorPath, _contentRoot), models.ObjectDetectorInputSize,
                models.Resolve(models.ObjectClassesPath, _contentRoot), models,
                _loggerFactory.CreateLogger<YoloDetector>());

            status.ObjectDetectorReady = true;
            status.ObjectModelId = Path.GetFileNameWithoutExtension(models.ObjectDetectorPath);
            status.ObjectClasses = _objectDetector.ClassNames;
        }, ex => status.ObjectError = ex.Message);

        // El extractor de características de objetos es opcional: sin él se detectan
        // objetos igual, pero no se reconocen por su apariencia.
        if (!string.IsNullOrWhiteSpace(models.ObjectEmbedderPath))
        {
            TryLoad("características de objetos", () =>
            {
                _objectEmbedder = new ObjectEmbedder(
                    models.Resolve(models.ObjectEmbedderPath, _contentRoot), models,
                    _loggerFactory.CreateLogger<ObjectEmbedder>());
                status.ObjectEmbedderReady = true;
            }, ex => status.ObjectEmbedderError = ex.Message);
        }

        TryLoad("texto", () =>
        {
            _textDetector = new DbTextDetector(
                models.Resolve(models.TextDetectorPath, _contentRoot), models,
                _loggerFactory.CreateLogger<DbTextDetector>());
            status.TextDetectorReady = true;

            _textRecognizer = new CtcTextRecognizer(
                models.Resolve(models.TextRecognizerPath, _contentRoot),
                models.Resolve(models.TextCharsetPath, _contentRoot),
                CtcOptions.ForText(models), models, _loggerFactory.CreateLogger<CtcTextRecognizer>());
            status.TextRecognizerReady = true;
        }, ex => status.TextError = ex.Message);

        Status = status;
        _loaded = true;
    }

    private void TryLoad(string what, Action load, Action<Exception> onError)
    {
        try
        {
            load();
        }
        catch (Exception ex)
        {
            onError(ex);
            _logger.LogWarning("No se pudieron cargar los modelos de {What}: {Message}", what, ex.Message);
        }
    }

    private void Disable(Exception ex, string what, Action<ModelStatus> mark)
    {
        _logger.LogError(ex, "Fallo en la detección de {What}; se desactiva hasta la próxima recarga", what);
        lock (_gate) mark(Status);
    }

    private void DisposeModels()
    {
        _faceDetector?.Dispose(); _faceDetector = null;
        _faceEmbedder?.Dispose(); _faceEmbedder = null;
        _plateDetector?.Dispose(); _plateDetector = null;
        _plateOcr?.Dispose(); _plateOcr = null;
        _objectDetector?.Dispose(); _objectDetector = null;
        _objectEmbedder?.Dispose(); _objectEmbedder = null;
        _textDetector?.Dispose(); _textDetector = null;
        _textRecognizer?.Dispose(); _textRecognizer = null;
        _codeReader = null;
        Status = new ModelStatus();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeModels();
        }
    }
}
