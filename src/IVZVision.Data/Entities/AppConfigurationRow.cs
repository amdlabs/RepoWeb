namespace IVZVision.Data.Entities;

/// <summary>
/// Fila única con la configuración vigente de la aplicación (cámaras, modelos,
/// umbrales, almacenamiento) serializada en JSON, con las contraseñas cifradas.
/// Es la copia autoritativa: el fichero de App_Data actúa como caché de arranque
/// para cuando la base de datos aún no está disponible.
/// </summary>
public class AppConfigurationRow
{
    public int Id { get; set; } = 1;

    public string Json { get; set; } = "";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
