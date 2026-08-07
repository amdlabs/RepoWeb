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
    public float PlateOcrMinConfidence { get; set; } = 0.55f;

    public int PlateMinCharacters { get; set; } = 4;

    public int PlateMaxCharacters { get; set; } = 10;

    /// <summary>
    /// Una matrícula se confirma cuando el mismo texto se lee este número de veces
    /// dentro de la ventana de estabilización. Evita lecturas sueltas erróneas.
    /// </summary>
    public int PlateConfirmationHits { get; set; } = 2;

    public int PlateConfirmationWindowSeconds { get; set; } = 5;

    // ---- Eventos -------------------------------------------------------
    /// <summary>Segundos durante los que no se repite un evento del mismo sujeto en la misma cámara.</summary>
    public int EventCooldownSeconds { get; set; } = 30;

    /// <summary>Registra también rostros/matrículas no reconocidos en la base de datos.</summary>
    public bool RegisterUnknown { get; set; } = true;

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
