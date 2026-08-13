namespace IVZVision.Core.Configuration;

/// <summary>Formato de matrícula esperado, usado para validar y corregir las lecturas del OCR.</summary>
public enum PlateFormat
{
    /// <summary>Sin formato fijo: sólo se comprueba longitud y que haya algún dígito.</summary>
    Generic = 0,
    /// <summary>Uruguay: 3 letras + 4 números (ABC1234).</summary>
    Uruguay = 1,
    /// <summary>España actual: 4 números + 3 letras (1234ABC).</summary>
    Spain = 2,
    /// <summary>Argentina Mercosur: 2 letras + 3 números + 2 letras (AB123CD).</summary>
    Argentina = 3,
    /// <summary>Mercosur genérico: 3 letras + número + carácter + 2 números (ABC1D23).</summary>
    Mercosur = 4,
    /// <summary>Expresión regular propia indicada en <see cref="RecognitionConfig.PlateCustomPattern"/>.</summary>
    Custom = 5,
}

/// <summary>Umbrales y comportamiento del motor de reconocimiento.</summary>
public sealed class RecognitionConfig
{
    // ---- Rostros -------------------------------------------------------
    /// <summary>Confianza mínima del detector de rostros (0-1).</summary>
    public float FaceDetectionThreshold { get; set; } = 0.75f;

    /// <summary>IoU para la supresión de no-máximos del detector de rostros.</summary>
    public float FaceNmsThreshold { get; set; } = 0.3f;

    /// <summary>Lado mínimo en píxeles de un rostro para intentar identificarlo.</summary>
    public int MinFaceSize { get; set; } = 48;

    /// <summary>
    /// Similitud coseno mínima para dar por identificada a una persona.
    /// El valor recomendado por OpenCV para SFace es 0,363.
    /// </summary>
    public float FaceMatchThreshold { get; set; } = 0.363f;

    /// <summary>
    /// Similitud a partir de la cual dos rostros desconocidos se consideran la misma
    /// persona y se agrupan en una sola ficha pendiente de nombrar.
    /// </summary>
    public float FaceClusterThreshold { get; set; } = 0.28f;

    // ---- Matrículas ----------------------------------------------------
    public float PlateDetectionThreshold { get; set; } = 0.45f;

    public float PlateNmsThreshold { get; set; } = 0.45f;

    /// <summary>Confianza media mínima del OCR para aceptar la lectura (0-1).</summary>
    public float PlateOcrMinConfidence { get; set; } = 0.55f;

    /// <summary>Formato de matrícula del país donde se instala.</summary>
    public PlateFormat PlateFormat { get; set; } = PlateFormat.Uruguay;

    /// <summary>Expresión regular usada cuando el formato es <see cref="PlateFormat.Custom"/>.</summary>
    public string PlateCustomPattern { get; set; } = "^[A-Z]{3}[0-9]{4}$";

    /// <summary>
    /// Corrige las confusiones típicas del OCR (O/0, I/1, S/5, B/8) usando el formato:
    /// en las posiciones de letra convierte dígitos a letras y viceversa.
    /// </summary>
    public bool PlateFixOcrConfusions { get; set; } = true;

    public int PlateMinCharacters { get; set; } = 4;

    public int PlateMaxCharacters { get; set; } = 10;

    /// <summary>
    /// Una matrícula se confirma cuando el mismo texto se lee este número de veces
    /// dentro de la ventana de estabilización. Evita lecturas sueltas erróneas.
    /// </summary>
    public int PlateConfirmationHits { get; set; } = 2;

    public int PlateConfirmationWindowSeconds { get; set; } = 5;

    // ---- Objetos -------------------------------------------------------
    public float ObjectDetectionThreshold { get; set; } = 0.4f;

    public float ObjectNmsThreshold { get; set; } = 0.45f;

    /// <summary>
    /// Clases del detector que se registran como eventos. Vacío = todas.
    /// Se comparan en minúsculas contra el fichero de clases del modelo.
    /// </summary>
    public List<string> ObjectClassesOfInterest { get; set; } = new()
    {
        "person", "cat", "dog", "bird", "horse", "sheep", "cow", "bear",
        "car", "motorcycle", "bus", "truck", "bicycle", "backpack", "handbag", "suitcase", "knife",
    };

    /// <summary>Similitud mínima para reconocer un objeto ya nombrado por su apariencia.</summary>
    public float ObjectMatchThreshold { get; set; } = 0.62f;

    // ---- Códigos y texto ------------------------------------------------
    /// <summary>Longitud mínima del contenido de un código para darlo por bueno.</summary>
    public int CodeMinLength { get; set; } = 3;

    public float TextDetectionThreshold { get; set; } = 0.3f;

    /// <summary>Confianza mínima del OCR para aceptar una línea de texto.</summary>
    public float TextMinConfidence { get; set; } = 0.6f;

    /// <summary>Caracteres mínimos de una línea de texto para registrarla.</summary>
    public int TextMinLength { get; set; } = 3;

    // ---- Eventos -------------------------------------------------------
    /// <summary>Segundos durante los que no se repite un evento del mismo sujeto en la misma cámara.</summary>
    public int EventCooldownSeconds { get; set; } = 30;

    /// <summary>Registra también rostros/matrículas no reconocidos en la base de datos.</summary>
    public bool RegisterUnknown { get; set; } = true;

    /// <summary>
    /// Envía los sujetos no reconocidos a la lista de pendientes para poder ponerles
    /// nombre; a partir de ese momento el sistema los reconoce.
    /// </summary>
    public bool QueueUnknownForLearning { get; set; } = true;

    /// <summary>Máximo de fichas pendientes que se conservan (las más antiguas se descartan).</summary>
    public int MaxPendingSubjects { get; set; } = 500;

    /// <summary>Guarda en disco el recorte del rostro/matrícula de cada evento.</summary>
    public bool SaveSnapshots { get; set; } = true;

    /// <summary>Calidad JPEG del vídeo emitido a la web (1-100).</summary>
    public int StreamJpegQuality { get; set; } = 75;

    /// <summary>Fotogramas por segundo máximos que se envían al navegador.</summary>
    public double StreamFps { get; set; } = 12;

    /// <summary>Dibuja los cuadrantes y etiquetas sobre el vídeo.</summary>
    public bool DrawOverlay { get; set; } = true;

    /// <summary>Número de detecciones recientes que se conservan en memoria por cámara.</summary>
    public int RecentDetectionsBuffer { get; set; } = 40;
}
