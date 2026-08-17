namespace IVZVision.Core.Configuration;

public sealed class StorageConfig
{
    /// <summary>Carpeta donde se guardan los recortes y capturas de los eventos.</summary>
    public string SnapshotsDirectory { get; set; } = "App_Data/snapshots";

    /// <summary>Días que se conservan los eventos y sus imágenes (0 = sin límite).</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Hora del día (0-23) a la que se ejecuta la purga.</summary>
    public int PurgeHour { get; set; } = 3;

    public string Resolve(string contentRoot)
    {
        var dir = string.IsNullOrWhiteSpace(SnapshotsDirectory) ? "App_Data/snapshots" : SnapshotsDirectory;
        return Path.IsPathRooted(dir) ? dir : Path.GetFullPath(Path.Combine(contentRoot, dir));
    }
}
