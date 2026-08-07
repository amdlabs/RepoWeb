using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Core.Util;
using IVZVision.Data;
using IVZVision.Vision.Faces;
using IVZVision.Vision.Imaging;
using IVZVision.Vision.Objects;
using IVZVision.Vision.Plates;
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
    /// <summary>Dato adicional resuelto durante el análisis (p. ej. marca/modelo del vehículo).</summary>
    public string? Annotation { get; init; }
    public IdentityMatch Match { get; init; } = IdentityMatch.Unknown;
}

/// <summary>Estado de carga de cada modelo, para mostrarlo en la pantalla de configuración.</summary>
public sealed class ModelStatus
{
    public bool FaceDetectorReady { get; set; }
    public bool FaceEmbedderReady { get; set; }
    public bool PlateDetectorReady { get; set; }
    public bool PlateOcrReady { get; set; }
    public bool ObjectDetectorReady { get; set; }
    public bool TextDetectorReady { get; set; }

    public string? FaceError { get; set; }
    public string? PlateError { get; set; }
    public string? ObjectError { get; set; }
    public string? TextError { get; set; }

    public bool FacesAvailable => FaceDetectorReady && FaceEmbedderReady;
    public bool PlatesAvailable => PlateDetectorReady && PlateOcrReady;
    public bool ObjectsAvailable => ObjectDetectorReady;
    /// <summary>La lectura de textos usa el detector propio y el OCR de matrículas.</summary>
    public bool TextsAvailable => TextDetectorReady && PlateOcrReady;
    public string? FaceModelId { get; set; }
}

public sealed record FaceEnrollment(bool Success, string Message, float[]? Embedding = null,
                                    byte[]? AlignedJpeg = null, float Score = 0, string? ModelId = null);

/// <summary>
/// Punto único de acceso a los modelos locales. Carga perezosa, recarga sin
/// reiniciar la aplicación cuando cambia la configuración y degradación
/// controlada: si faltan los modelos de matrículas los rostros siguen funcionando.
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
    private YoloPlateDetector? _plateDetector;
    private CtcPlateOcr? _plateOcr;
    private YoloObjectDetector? _objectDetector;
    private SceneTextDetector? _textDetector;
    private VehicleModelClassifier? _vehicleModel;
    private bool _loaded;

    private static readonly HashSet<string> VehicleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "coche", "camion", "moto", "autobus", "car", "truck", "motorcycle", "bus",
    };
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

    private DateTimeOffset _lastRetryAt = DateTimeOffset.MinValue;
    private readonly List<string> _missingModelFiles = new();

    /// <summary>Fuerza la carga inmediata y devuelve el estado resultante (lo usa la pantalla de configuración).</summary>
    public ModelStatus EnsureLoaded()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                Load();
            }
            else if (_missingModelFiles.Count > 0
                     && DateTimeOffset.UtcNow - _lastRetryAt > TimeSpan.FromSeconds(30)
                     && _missingModelFiles.Any(File.Exists))
            {
                // Un modelo que faltaba ya está en disco (p. ej. recién descargado):
                // se recarga sin necesidad de reiniciar la aplicación.
                _lastRetryAt = DateTimeOffset.UtcNow;
                DisposeModels();
                Load();
            }

            return Status;
        }
    }

    /// <summary>
    /// Una sola inferencia a la vez en todo el proceso: con varias cámaras, las
    /// ejecuciones concurrentes sobre la misma sesión provocan violaciones de acceso
    /// con el proveedor DirectML y saturan la CPU con el resto. Los workers esperan
    /// su turno; su ritmo de análisis se autolimita al rendimiento real del equipo.
    /// </summary>
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);

    public IReadOnlyList<AnalysisItem> Analyze(Mat analysisFrame, CameraConfig camera)
    {
        EnsureLoaded();

        var rec = _config.Current.Recognition;
        var items = new List<AnalysisItem>();

        _inferenceGate.Wait();
        try
        {
            if (camera.EnableFaceRecognition && Status.FacesAvailable)
                AnalyzeFaces(analysisFrame, rec, items);

            if (camera.EnablePlateRecognition && Status.PlatesAvailable)
                AnalyzePlates(analysisFrame, rec, items);

            if (camera.EnableObjectDetection && Status.ObjectsAvailable)
                AnalyzeObjects(analysisFrame, rec, items);

            if (camera.EnableTextReading && Status.TextsAvailable)
                AnalyzeTexts(analysisFrame, rec, items);
        }
        finally
        {
            _inferenceGate.Release();
        }

        return items;
    }

    /// <summary>Localiza los textos de la escena y los lee con el OCR CRNN.</summary>
    private void AnalyzeTexts(Mat frame, RecognitionConfig rec, List<AnalysisItem> items)
    {
        SceneTextDetector? detector;
        CtcPlateOcr? ocr;
        lock (_gate) { detector = _textDetector; ocr = _plateOcr; }
        if (detector is null || ocr is null) return;

        try
        {
            var boxes = detector.Detect(frame, rec.TextDetectionThreshold, rec.MaxTextsPerFrame);

            foreach (var box in boxes)
            {
                using var crop = ImageOps.SafeCrop(frame, box.Expand(0.04f, frame.Width, frame.Height));
                if (crop is null) continue;

                PlateReading reading;
                try { reading = ocr.Read(crop); }
                catch (Exception) { continue; }

                var text = reading.Text?.Trim();
                if (string.IsNullOrEmpty(text) || text.Length < 2) continue;
                if (reading.Confidence < rec.TextMinConfidence) continue;

                // Los relojes y fechas sobreimpresos del DVR cambian cada minuto y
                // generarían miles de eventos sin valor: se descarta lo mayormente numérico.
                var digits = text.Count(c => char.IsDigit(c) || c is ':' or '-' or '/' or '+');
                if (digits >= text.Length * 0.5) continue;

                items.Add(new AnalysisItem
                {
                    Kind = ObservationKind.Text,
                    Box = box,
                    Score = reading.Confidence,
                    Match = new IdentityMatch { IsKnown = false, Label = text, Score = reading.Confidence },
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo en la lectura de textos; se desactiva hasta la próxima recarga");
            lock (_gate)
            {
                Status.TextDetectorReady = false;
                Status.TextError = ex.Message;
            }
        }
    }

    private void AnalyzeObjects(Mat frame, RecognitionConfig rec, List<AnalysisItem> items)
    {
        YoloObjectDetector? detector;
        VehicleModelClassifier? vehicleModel;
        lock (_gate) { detector = _objectDetector; vehicleModel = _vehicleModel; }
        if (detector is null) return;

        try
        {
            var objects = detector.Detect(frame, rec.ObjectDetectionThreshold, rec.ObjectNmsThreshold);

            foreach (var obj in objects)
            {
                if (Math.Min(obj.Box.Width, obj.Box.Height) < Math.Max(8, rec.MinObjectSize)) continue;

                // Marca/modelo del vehículo, si hay clasificador instalado (opcional).
                string? annotation = null;
                if (vehicleModel is not null && VehicleClasses.Contains(obj.ClassName)
                    && Math.Min(obj.Box.Width, obj.Box.Height) >= 64)
                {
                    try
                    {
                        using var crop = ImageOps.SafeCrop(frame, obj.Box);
                        if (crop is not null)
                        {
                            var (label, confidence) = vehicleModel.Classify(crop);
                            if (confidence >= rec.VehicleModelMinConfidence)
                                annotation = label;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "No se pudo clasificar el modelo de un vehículo");
                    }
                }

                items.Add(new AnalysisItem
                {
                    Kind = ObservationKind.Object,
                    Box = obj.Box,
                    Score = obj.Score,
                    ObjectClass = obj.ClassName,
                    Annotation = annotation,
                    Match = _index.MatchObject(obj.ClassName),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo en la detección de objetos; se desactiva hasta la próxima recarga");
            lock (_gate)
            {
                Status.ObjectDetectorReady = false;
                Status.ObjectError = ex.Message;
            }
        }
    }

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
                try
                {
                    var embedding = embedder.Embed(frame, face.Landmarks);
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
                    Match = match,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo en la detección de rostros; se desactiva hasta la próxima recarga");
            lock (_gate)
            {
                Status.FaceDetectorReady = false;
                Status.FaceError = ex.Message;
            }
        }
    }

    private void AnalyzePlates(Mat frame, RecognitionConfig rec, List<AnalysisItem> items)
    {
        YoloPlateDetector? detector;
        CtcPlateOcr? ocr;
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

                PlateReading reading;
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
                var valid = reading.Confidence >= rec.PlateOcrMinConfidence
                            && PlateText.LooksValid(normalized, rec.PlateMinCharacters, rec.PlateMaxCharacters);

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
            _logger.LogError(ex, "Fallo en la detección de matrículas; se desactiva hasta la próxima recarga");
            lock (_gate)
            {
                Status.PlateDetectorReady = false;
                Status.PlateError = ex.Message;
            }
        }
    }

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

        _inferenceGate.Wait();
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
        finally
        {
            _inferenceGate.Release();
        }
    }

    private void Load()
    {
        var models = _config.Current.Models;
        var status = new ModelStatus();

        // Registro de ficheros ausentes para poder recargar cuando aparezcan.
        _missingModelFiles.Clear();
        foreach (var path in new[]
                 {
                     models.Resolve(models.FaceDetectorPath, _contentRoot),
                     models.Resolve(models.FaceEmbedderPath, _contentRoot),
                     models.Resolve(models.PlateDetectorPath, _contentRoot),
                     models.Resolve(models.PlateOcrPath, _contentRoot),
                     models.Resolve(models.ObjectDetectorPath, _contentRoot),
                     models.Resolve(models.SceneTextDetectorPath, _contentRoot),
                 })
        {
            if (!string.IsNullOrEmpty(path) && !File.Exists(path))
                _missingModelFiles.Add(path);
        }

        try
        {
            var detectorPath = models.Resolve(models.FaceDetectorPath, _contentRoot);
            _faceDetector = new YuNetFaceDetector(detectorPath, models, _loggerFactory.CreateLogger<YuNetFaceDetector>());
            status.FaceDetectorReady = true;

            var embedderPath = models.Resolve(models.FaceEmbedderPath, _contentRoot);
            _faceEmbedder = new SFaceEmbedder(embedderPath, models, _loggerFactory.CreateLogger<SFaceEmbedder>());
            status.FaceEmbedderReady = true;
            status.FaceModelId = _faceEmbedder.ModelId;
        }
        catch (Exception ex)
        {
            status.FaceError = ex.Message;
            _logger.LogError(ex, "No se pudieron cargar los modelos de rostros");
        }

        try
        {
            var platePath = models.Resolve(models.PlateDetectorPath, _contentRoot);
            _plateDetector = new YoloPlateDetector(platePath, models, _loggerFactory.CreateLogger<YoloPlateDetector>());
            status.PlateDetectorReady = true;

            var ocrPath = models.Resolve(models.PlateOcrPath, _contentRoot);
            var charsetPath = models.Resolve(models.PlateOcrCharsetPath, _contentRoot);
            _plateOcr = new CtcPlateOcr(ocrPath, charsetPath, models, _loggerFactory.CreateLogger<CtcPlateOcr>());
            status.PlateOcrReady = true;
        }
        catch (Exception ex)
        {
            status.PlateError = ex.Message;
            _logger.LogError(ex, "No se pudieron cargar los modelos de matrículas");
        }

        try
        {
            var objectPath = models.Resolve(models.ObjectDetectorPath, _contentRoot);
            var labelsPath = models.Resolve(models.ObjectLabelsPath, _contentRoot);
            _objectDetector = new YoloObjectDetector(objectPath, labelsPath, models,
                _loggerFactory.CreateLogger<YoloObjectDetector>());
            status.ObjectDetectorReady = true;
        }
        catch (Exception ex)
        {
            status.ObjectError = ex.Message;
            _logger.LogError(ex, "No se pudo cargar el modelo de detección de objetos");
        }

        // Clasificador de marca/modelo de vehículo: totalmente opcional, solo si el
        // usuario ha dejado el modelo y sus etiquetas en la carpeta.
        var vehicleModelPath = models.Resolve(models.VehicleModelClassifierPath, _contentRoot);
        if (File.Exists(vehicleModelPath))
        {
            try
            {
                _vehicleModel = new VehicleModelClassifier(vehicleModelPath,
                    models.Resolve(models.VehicleModelLabelsPath, _contentRoot), models,
                    _loggerFactory.CreateLogger<VehicleModelClassifier>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo cargar el clasificador de modelos de vehículo");
            }
        }

        try
        {
            var textPath = models.Resolve(models.SceneTextDetectorPath, _contentRoot);
            _textDetector = new SceneTextDetector(textPath, models,
                _loggerFactory.CreateLogger<SceneTextDetector>());
            status.TextDetectorReady = true;
        }
        catch (Exception ex)
        {
            status.TextError = ex.Message;
            _logger.LogError(ex, "No se pudo cargar el detector de texto");
        }

        Status = status;
        _loaded = true;
    }

    private void DisposeModels()
    {
        _faceDetector?.Dispose(); _faceDetector = null;
        _faceEmbedder?.Dispose(); _faceEmbedder = null;
        _plateDetector?.Dispose(); _plateDetector = null;
        _plateOcr?.Dispose(); _plateOcr = null;
        _objectDetector?.Dispose(); _objectDetector = null;
        _textDetector?.Dispose(); _textDetector = null;
        _vehicleModel?.Dispose(); _vehicleModel = null;
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
