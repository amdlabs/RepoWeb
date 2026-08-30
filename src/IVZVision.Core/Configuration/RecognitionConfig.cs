namespace IVZVision.Core.Configuration;

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

    // ---- Matrículas ----------------------------------------------------
    public float PlateDetectionThreshold { get; set; } = 0.45f;

    public float PlateNmsThreshold { get; set; } = 0.45f;

    /// <summary>Confianza media mínima del OCR para aceptar la lectura (0-1).</summary>
    public float PlateOcrMinConfidence { get; set; } = 0.75f;

    public int PlateMinCharacters { get; set; } = 4;

    public int PlateMaxCharacters { get; set; } = 10;

    /// <summary>
    /// Exige el patrón de matrícula del país (por defecto el uruguayo: tres letras
    /// y cuatro dígitos). Las lecturas que no lo cumplan se descartan y no se guardan.
    /// </summary>
    public bool EnforcePlatePattern { get; set; } = true;

    /// <summary>Letras iniciales que debe tener la matrícula.</summary>
    public int PlatePatternLetters { get; set; } = 3;

    /// <summary>Dígitos finales que debe tener la matrícula.</summary>
    public int PlatePatternDigits { get; set; } = 4;

    /// <summary>
    /// Aprendizaje continuo del LPR: una lectura que difiere en un solo carácter de
    /// una matrícula ya conocida se adopta como esa matrícula. Mejora el acierto a
    /// medida que el sistema ve más pasadas de los mismos vehículos.
    /// </summary>
    public bool LearnFromKnownPlates { get; set; } = true;

    /// <summary>
    /// Una matrícula se confirma cuando el mismo texto se lee este número de veces
    /// dentro de la ventana de estabilización. Evita lecturas sueltas erróneas.
    /// </summary>
    public int PlateConfirmationHits { get; set; } = 2;

    public int PlateConfirmationWindowSeconds { get; set; } = 5;

    // ---- Objetos genéricos ----------------------------------------------
    /// <summary>Confianza mínima del detector de objetos COCO (0-1).</summary>
    public float ObjectDetectionThreshold { get; set; } = 0.45f;

    public float ObjectNmsThreshold { get; set; } = 0.45f;

    /// <summary>Lado mínimo en píxeles de un objeto para tenerlo en cuenta.</summary>
    public int MinObjectSize { get; set; } = 32;

    // ---- Modelo de vehículo -----------------------------------------------
    /// <summary>Confianza mínima del clasificador de marca/modelo para aceptar la identificación (0-1).</summary>
    public float VehicleModelMinConfidence { get; set; } = 0.45f;

    // ---- Textos de la escena ---------------------------------------------
    /// <summary>Umbral del mapa de probabilidad del detector de texto (0-1).</summary>
    public float TextDetectionThreshold { get; set; } = 0.3f;

    /// <summary>Confianza mínima del OCR para aceptar un texto leído (0-1).</summary>
    public float TextMinConfidence { get; set; } = 0.6f;

    /// <summary>Máximo de textos que se leen por fotograma (los más grandes primero).</summary>
    public int MaxTextsPerFrame { get; set; } = 8;

    // ---- Eventos -------------------------------------------------------
    /// <summary>Segundos durante los que no se repite un evento del mismo sujeto en la misma cámara.</summary>
    public int EventCooldownSeconds { get; set; } = 30;

    /// <summary>Registra también rostros/matrículas no reconocidos en la base de datos.</summary>
    public bool RegisterUnknown { get; set; } = true;

    /// <summary>
    /// Similitud coseno a partir de la cual un rostro desconocido se considera «el mismo
    /// de antes» y no se registra otra vez (deduplicación de desconocidos).
    /// </summary>
    public float UnknownFaceDedupSimilarity { get; set; } = 0.45f;

    /// <summary>Minutos que se recuerda a un desconocido para no volver a anexarlo.</summary>
    public int UnknownFaceDedupWindowMinutes { get; set; } = 120;

    /// <summary>
    /// Parecido mínimo para considerar que dos rostros son la misma persona y
    /// agruparlos bajo la misma ficha, aunque aparezcan en cámaras distintas.
    /// </summary>
    public float FaceClusterSimilarity { get; set; } = 0.36f;

    /// <summary>
    /// Parecido entre dos grupos de rostros a partir del cual se fusionan solos:
    /// si dos fichas se parecen tanto, son la misma persona y no tiene sentido
    /// esperar a que alguien las una a mano.
    /// </summary>
    public float FaceClusterAutoMergeSimilarity { get; set; } = 0.50f;

    /// <summary>
    /// Memoria de escena: los objetos quietos (libros, cajas…) se aprenden y dejan de
    /// anunciarse mientras sigan en su sitio; se avisa cuando faltan o cuando vuelven.
    /// </summary>
    public bool SceneMemoryEnabled { get; set; } = true;

    /// <summary>Segundos sin ver un objeto quieto antes de darlo por ausente.</summary>
    public int SceneObjectMissingSeconds { get; set; } = 90;

    /// <summary>Tiempo de guarda específico para personas sin identificar (segundos).</summary>
    public int UnknownPersonCooldownSeconds { get; set; } = 300;

    // ---- Filtros de ruido ---------------------------------------------------
    /// <summary>
    /// Registrar sólo objetos prioritarios (personas, vehículos y animales); el resto
    /// de clases sólo se registra si el usuario les ha puesto etiqueta.
    /// </summary>
    public bool PriorityObjectsOnly { get; set; } = true;

    /// <summary>Horas durante las que un mismo texto de la misma cámara no se vuelve a registrar (rótulos fijos).</summary>
    public int TextRepeatSuppressionHours { get; set; } = 24;

    /// <summary>Nitidez mínima del recorte (varianza del laplaciano) para registrar personas/rostros. 0 = sin filtro.</summary>
    public double MinCropSharpness { get; set; } = 25;

    /// <summary>Brillo medio mínimo del recorte (0-255) para registrar personas/rostros. 0 = sin filtro.</summary>
    public double MinCropBrightness { get; set; } = 12;

    /// <summary>Guarda en disco el recorte del rostro/matrícula de cada evento.</summary>
    public bool SaveSnapshots { get; set; } = true;

    /// <summary>Calidad JPEG del vídeo emitido a la web (1-100).</summary>
    public int StreamJpegQuality { get; set; } = 75;

    /// <summary>Fotogramas por segundo máximos que se envían al navegador.</summary>
    public double StreamFps { get; set; } = 15;

    /// <summary>Dibuja los cuadrantes y etiquetas sobre el vídeo.</summary>
    public bool DrawOverlay { get; set; } = true;

    /// <summary>Número de detecciones recientes que se conservan en memoria por cámara.</summary>
    public int RecentDetectionsBuffer { get; set; } = 40;
}
