using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IVZVision.Core.Configuration;

/// <summary>
/// Almacén de configuración en fichero JSON. Es la única fuente de verdad de los
/// parámetros de conexión: la web escribe aquí y el resto de servicios reaccionan
/// al evento <see cref="Changed"/>.
/// </summary>
public interface IConfigStore
{
    AppConfig Current { get; }

    string FilePath { get; }

    /// <summary>Se dispara tras guardar una configuración nueva.</summary>
    event EventHandler<AppConfig>? Changed;

    Task SaveAsync(AppConfig config, CancellationToken ct = default);

    /// <summary>Aplica una mutación sobre una copia de la configuración actual y la guarda.</summary>
    Task<AppConfig> UpdateAsync(Action<AppConfig> mutate, CancellationToken ct = default);
}

public sealed class JsonFileConfigStore : IConfigStore
{
    private readonly ILogger<JsonFileConfigStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile AppConfig _current;

    public JsonFileConfigStore(string filePath, ILogger<JsonFileConfigStore> logger)
    {
        FilePath = filePath;
        _logger = logger;
        _current = Load(filePath, logger);
    }

    public AppConfig Current => _current;

    public string FilePath { get; }

    public event EventHandler<AppConfig>? Changed;

    public async Task SaveAsync(AppConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.UpdatedAt = DateTimeOffset.UtcNow;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(config, ConfigJson.Options);
            var tmp = FilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);

            // Escritura atómica: el fichero nunca queda a medias si se corta el proceso.
            File.Move(tmp, FilePath, overwrite: true);

            _current = config;
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Configuración guardada en {Path}", FilePath);
        Changed?.Invoke(this, config);
    }

    public async Task<AppConfig> UpdateAsync(Action<AppConfig> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var copy = _current.Clone();
        mutate(copy);
        await SaveAsync(copy, ct).ConfigureAwait(false);
        return copy;
    }

    private static AppConfig Load(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options);
                if (cfg is not null)
                {
                    logger.LogInformation("Configuración cargada de {Path}", path);
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo leer {Path}; se arranca con la configuración por defecto", path);
        }

        return new AppConfig();
    }
}
