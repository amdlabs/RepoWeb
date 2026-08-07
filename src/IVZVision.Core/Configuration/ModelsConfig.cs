namespace IVZVision.Core.Configuration;

public enum ExecutionProviderKind
{
    Cpu = 0,
    Cuda = 1,
    DirectMl = 2,
}

/// <summary>
/// Rutas de los modelos que se ejecutan en local y ajustes del runtime de inferencia.
/// Todas las rutas pueden ser absolutas o relativas a <see cref="ModelsDirectory"/>.
/// Cada bloque es independiente: si falta uno, el resto sigue funcionando.
/// </summary>
public sealed class ModelsConfig
{
    /// <summary>Carpeta base para resolver rutas relativas.</summary>
    public string ModelsDirectory { get; set; } = "Models";

    // ---- Rostros -------------------------------------------------------
    /// <summary>Detector de rostros YuNet (OpenCV Zoo, face_detection_yunet_*.onnx).</summary>
    public string FaceDetectorPath { get; set; } = "face_detection_yunet_2023mar.onnx";

    /// <summary>Extractor de embeddings SFace (OpenCV Zoo, face_recognition_sface_*.onnx).</summary>
    public string FaceEmbedderPath { get; set; } = "face_recognition_sface_2021dec.onnx";

    /// <summary>Lado del cuadro de entrada del detector de rostros (se ignora si el modelo lo fija).</summary>
    public int FaceDetectorInputWidth { get; set; } = 640;
    public int FaceDetectorInputHeight { get; set; } = 640;

    // ---- Matrículas ----------------------------------------------------
    /// <summary>Detector de matrículas en formato YOLO (v5/v8) exportado a ONNX.</summary>
    public string PlateDetectorPath { get; set; } = "license_plate_detector.onnx";

    public int PlateDetectorInputSize { get; set; } = 640;

    /// <summary>Modelo de reconocimiento de texto CRNN/CTC para las matrículas.</summary>
    public string PlateOcrPath { get; set; } = "plate_ocr_rec.onnx";

    /// <summary>Fichero de diccionario del OCR de matrículas: un carácter por línea.</summary>
    public string PlateOcrCharsetPath { get; set; } = "plate_ocr_charset.txt";

    public int PlateOcrInputHeight { get; set; } = 48;
    public int PlateOcrInputWidth { get; set; } = 320;

    /// <summary>El OCR espera imagen en escala de grises (1 canal) en vez de RGB.</summary>
    public bool PlateOcrGrayscale { get; set; } = false;

    /// <summary>Normalización del OCR: (pixel/255 - Mean) / Std.</summary>
    public float PlateOcrMean { get; set; } = 0.5f;
    public float PlateOcrStd { get; set; } = 0.5f;

    /// <summary>El índice 0 del diccionario es el símbolo "blank" de CTC (caso de PP-OCR).</summary>
    public bool PlateOcrBlankFirst { get; set; } = true;

    // ---- Objetos -------------------------------------------------------
    /// <summary>Detector de objetos multiclase YOLO (por ejemplo yolov8n.onnx entrenado en COCO).</summary>
    public string ObjectDetectorPath { get; set; } = "yolov8n.onnx";

    /// <summary>Fichero con los nombres de las clases del detector, uno por línea.</summary>
    public string ObjectClassesPath { get; set; } = "coco.names";

    public int ObjectDetectorInputSize { get; set; } = 640;

    /// <summary>
    /// Extractor de características de objetos (opcional). Con él, los objetos a los
    /// que se pone nombre se reconocen luego por su apariencia y se puede buscar por
    /// imagen de ejemplo. Sirve cualquier codificador de imagen ONNX (CLIP, MobileNet…).
    /// </summary>
    public string ObjectEmbedderPath { get; set; } = "";

    public int ObjectEmbedderInputSize { get; set; } = 224;

    /// <summary>Normalización del extractor de objetos: (pixel/255 - Mean) / Std.</summary>
    public float ObjectEmbedderMean { get; set; } = 0.481f;
    public float ObjectEmbedderStd { get; set; } = 0.269f;

    // ---- Texto / escritura ----------------------------------------------
    /// <summary>Detector de texto tipo DB/DBNet (por ejemplo el modelo "det" de PP-OCR).</summary>
    public string TextDetectorPath { get; set; } = "text_det.onnx";

    /// <summary>Lado máximo al que se reescala el fotograma para detectar texto.</summary>
    public int TextDetectorMaxSide { get; set; } = 960;

    /// <summary>Reconocedor de texto CRNN/CTC (modelo "rec" de PP-OCR).</summary>
    public string TextRecognizerPath { get; set; } = "text_rec.onnx";

    /// <summary>Diccionario del reconocedor de texto: un carácter por línea.</summary>
    public string TextCharsetPath { get; set; } = "text_charset.txt";

    public int TextRecognizerInputHeight { get; set; } = 48;
    public int TextRecognizerInputWidth { get; set; } = 320;
    public bool TextRecognizerGrayscale { get; set; } = false;
    public float TextRecognizerMean { get; set; } = 0.5f;
    public float TextRecognizerStd { get; set; } = 0.5f;
    public bool TextRecognizerBlankFirst { get; set; } = true;

    // ---- Runtime -------------------------------------------------------
    public ExecutionProviderKind ExecutionProvider { get; set; } = ExecutionProviderKind.Cpu;

    /// <summary>Id del dispositivo GPU cuando el proveedor no es CPU.</summary>
    public int GpuDeviceId { get; set; } = 0;

    /// <summary>Hilos intra-op de ONNX Runtime (0 = automático).</summary>
    public int IntraOpThreads { get; set; } = 0;

    public string Resolve(string path, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Path.IsPathRooted(path)) return path;

        var dir = string.IsNullOrWhiteSpace(ModelsDirectory) ? "Models" : ModelsDirectory;
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(contentRoot, dir);

        return Path.GetFullPath(Path.Combine(dir, path));
    }
}
