namespace IVZVision.Data.Entities;

/// <summary>
/// Copia histórica en base de datos de la configuración de dispositivos y del
/// sistema (el JSON completo, con las contraseñas ya cifradas). El fichero
/// sigue siendo la fuente operativa; esta tabla guarda el respaldo y el histórico.
/// </summary>
public class ConfigSnapshot
{
    public long Id { get; set; }

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Configuración completa serializada (cámaras, base de datos, modelos, almacenamiento).</summary>
    public string Json { get; set; } = "";
}
