namespace IVZVision.Core.Configuration;

public enum ExecutionProviderKind
{
    Cpu = 0,
    Cuda = 1,
    DirectMl = 2,
}

/// <summary>
/// Rutas de los modelos que se ejecutan en local y ajustes del runtime de inferencia.
/// Todas las rutas pueden ser absolutas o relativas al directorio de la aplicación.
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

    /// <summary>Lado del cuadro de entrada del detector de rostros.</summary>
    public int FaceDetectorInputWidth { get; set; } = 640;
    public int FaceDetectorInputHeight { get; set; } = 640;

    // ---- Matrículas ----------------------------------------------------
    /// <summary>Detector de matrículas en formato YOLO (v5/v8) exportado a ONNX.</summary>
    public string PlateDetectorPath { get; set; } = "license_plate_detector.onnx";

    public int PlateDetectorInputSize { get; set; } = 640;

    /// <summary>Modelo de reconocimiento de texto CRNN/CTC (por defecto PP-OCRv4 rec).</summary>
    public string PlateOcrPath { get; set; } = "plate_ocr_rec.onnx";

    /// <summary>Fichero de diccionario del OCR: un carácter por línea.</summary>
    public string PlateOcrCharsetPath { get; set; } = "plate_ocr_charset_en.txt";

    public int PlateOcrInputHeight { get; set; } = 48;
    public int PlateOcrInputWidth { get; set; } = 320;

    /// <summary>El OCR espera imagen en escala de grises (1 canal) en vez de RGB.</summary>
    public bool PlateOcrGrayscale { get; set; } = false;

    /// <summary>Normalización del OCR: (pixel/255 - Mean) / Std.</summary>
    public float PlateOcrMean { get; set; } = 0.5f;
    public float PlateOcrStd { get; set; } = 0.5f;

    /// <summary>El índice 0 del diccionario es el símbolo "blank" de CTC (caso de PP-OCR).</summary>
    public bool PlateOcrBlankFirst { get; set; } = true;

    // ---- Objetos genéricos ----------------------------------------------
    /// <summary>Detector de objetos COCO en formato YOLO (v5/v8/v11) exportado a ONNX.</summary>
    public string ObjectDetectorPath { get; set; } = "yolov5s.onnx";

    public int ObjectDetectorInputSize { get; set; } = 640;

    /// <summary>Fichero opcional con los nombres de las clases (uno por línea). Si falta se usan las 80 clases COCO en español.</summary>
    public string ObjectLabelsPath { get; set; } = "object_labels.txt";

    // ---- Textos de la escena ---------------------------------------------
    /// <summary>Detector de texto DBNet (PP-OCR det) exportado a ONNX; el OCR de matrículas lee lo localizado.</summary>
    public string SceneTextDetectorPath { get; set; } = "text_detector.onnx";

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
