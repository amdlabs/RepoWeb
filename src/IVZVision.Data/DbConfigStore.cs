using System.Text.Json;
using IVZVision.Core.Configuration;
using IVZVision.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IVZVision.Data;

/// <summary>
/// Almacén de configuración con la base de datos como copia autoritativa
/// (tabla <c>AppConfiguration</c>) y el fichero JSON como caché de arranque para
/// cuando la base aún no responde. Si se define la cadena de conexión como
/// parámetro de despliegue (web.config / appsettings), ésta manda siempre.
/// </summary>
public sealed class DbConfigStore : IConfigStore
{
    private readonly JsonFileConfigStore _file;
    private readonly string? _forcedConnectionString;
    private readonly ILogger<DbConfigStore> _logger;

    public DbConfigStore(JsonFileConfigStore file, string? forcedConnectionString, ILogger<DbConfigStore> logger)
    {
        _file = file;
        _forcedConnectionString = string.IsNullOrWhiteSpace(forcedConnectionString) ? null : forcedConnectionString.Trim();
        _logger = logger;

        ApplyForced(_file.Current);
        _file.Changed += (_, cfg) => Changed?.Invoke(this, cfg);

        // La adopción de la copia en base de datos no debe bloquear el arranque.
        _ = Task.Run(() => AdoptFromDatabaseAsync(CancellationToken.None));
    }

    public AppConfig Current => _file.Current;

    public string FilePath => _file.FilePath;

    /// <summary>True cuando la cadena de conexión viene fijada por web.config / appsettings.</summary>
    public bool ConnectionStringIsForced => _forcedConnectionString is not null;

    public event EventHandler<AppConfig>? Changed;

    public async Task SaveAsync(AppConfig config, CancellationToken ct = default)
    {
        ApplyForced(config);

        // Primero el fichero (atómico y siempre disponible); después la base.
        await _file.SaveAsync(config, ct).ConfigureAwait(false);
        await TrySaveToDatabaseAsync(config, ct).ConfigureAwait(false);
    }

    public async Task<AppConfig> UpdateAsync(Action<AppConfig> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var copy = Current.Clone();
        mutate(copy);
        await SaveAsync(copy, ct).ConfigureAwait(false);
        return copy;
    }

    private void ApplyForced(AppConfig config)
    {
        if (_forcedConnectionString is not null)
            config.Database.ConnectionStringOverride = _forcedConnectionString;
    }

    private string ResolveConnectionString() =>
        _forcedConnectionString ?? Current.Database.BuildConnectionString();

    /// <summary>Al arrancar: si la base tiene una configuración más reciente que el fichero, se adopta.</summary>
    private async Task AdoptFromDatabaseAsync(CancellationToken ct)
    {
        try
        {
            await using var db = VisionDbContextFactory.Create(ResolveConnectionString());

            var row = await db.AppConfiguration.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == 1, ct).ConfigureAwait(false);
            if (row is null || string.IsNullOrWhiteSpace(row.Json)) return;

            var stored = JsonSerializer.Deserialize<AppConfig>(row.Json, ConfigJson.Options);
            if (stored is null) return;

            SecretProtector.UnprotectSecrets(stored);
            ApplyForced(stored);

            if (stored.UpdatedAt > Current.UpdatedAt)
            {
                _logger.LogInformation(
                    "La configuración de la base de datos ({Db:u}) es más reciente que la del fichero ({File:u}); se adopta.",
                    stored.UpdatedAt, Current.UpdatedAt);
                await _file.SaveAsync(stored, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer la configuración de la base de datos; se sigue con la del fichero.");
        }
    }

    private async Task TrySaveToDatabaseAsync(AppConfig config, CancellationToken ct)
    {
        try
        {
            var persisted = config.Clone();
            SecretProtector.ProtectSecrets(persisted);
            var json = JsonSerializer.Serialize(persisted, ConfigJson.Options);

            await using var db = VisionDbContextFactory.Create(ResolveConnectionString());

            var row = await db.AppConfiguration.FirstOrDefaultAsync(r => r.Id == 1, ct).ConfigureAwait(false);
            if (row is null)
            {
                db.AppConfiguration.Add(new AppConfigurationRow { Id = 1, Json = json, UpdatedAt = DateTime.UtcNow });
            }
            else
            {
                row.Json = json;
                row.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Configuración guardada también en la base de datos (tabla AppConfiguration).");
        }
        catch (Exception ex)
        {
            // El fichero ya está guardado; la base se sincronizará en el próximo guardado.
            _logger.LogWarning(ex, "No se pudo guardar la configuración en la base de datos.");
        }
    }
}
