namespace IVZVision.Core.Configuration;

/// <summary>
/// Configuración completa de la aplicación. Se persiste en
/// <c>App_Data/ivzvision.settings.json</c> y se edita íntegramente desde la web.
/// </summary>
public sealed class AppConfig
{
    public DatabaseConfig Database { get; set; } = new();

    public List<CameraConfig> Cameras { get; set; } = new();

    public RecognitionConfig Recognition { get; set; } = new();

    public ModelsConfig Models { get; set; } = new();

    public StorageConfig Storage { get; set; } = new();

    public SecurityConfig Security { get; set; } = new();
    public PushConfig Push { get; set; } = new();

    /// <summary>Vista del muro por usuario (qué cámara en cada recuadro).</summary>
    public List<WallView> WallViews { get; set; } = new();

    /// <summary>
    /// Motor de reconocimiento encendido. Es persistente: si se apaga desde la web,
    /// sigue apagado tras reiniciar el equipo. La web permanece accesible en ambos
    /// casos; lo que arranca o se detiene es la captura y el análisis de las cámaras.
    /// </summary>
    public bool EngineEnabled { get; set; } = true;

    /// <summary>Marca de la última modificación, útil para saber si hay que reiniciar el pipeline.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AppConfig Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, ConfigJson.Options);
        return System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options) ?? new AppConfig();
    }

    public CameraConfig? FindCamera(Guid id) => Cameras.FirstOrDefault(c => c.Id == id);
}

public static class ConfigJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

/// <summary>Distribución del muro guardada por un usuario, para que le siga entre dispositivos.</summary>
public sealed class WallView
{
    public string Username { get; set; } = "";
    public int Layout { get; set; }
    public List<string> CameraOrder { get; set; } = new();
}
