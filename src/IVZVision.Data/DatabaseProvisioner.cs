using IVZVision.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IVZVision.Data;

public sealed record DatabaseCheckResult(bool Success, string Message, string? ServerVersion = null, string? Database = null);

/// <summary>Comprueba la conexión con SQL Server Express y crea la base y las tablas si faltan.</summary>
public sealed class DatabaseProvisioner
{
    private readonly IConfigStore _config;
    private readonly ILogger<DatabaseProvisioner> _logger;

    public DatabaseProvisioner(IConfigStore config, ILogger<DatabaseProvisioner> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Prueba de conexión que usa el botón "Probar conexión" de la pantalla de configuración.</summary>
    public static async Task<DatabaseCheckResult> TestAsync(DatabaseConfig db, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(db.BuildMasterConnectionString());
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var dbName = db.ResolveDatabaseName();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END";
            cmd.Parameters.AddWithValue("@name", dbName);
            var exists = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;

            var msg = exists
                ? $"Conexión correcta. La base «{dbName}» existe."
                : $"Conexión correcta al servidor, pero la base «{dbName}» todavía no existe (se creará al guardar).";

            return new DatabaseCheckResult(true, msg, conn.ServerVersion, dbName);
        }
        catch (Exception ex)
        {
            return new DatabaseCheckResult(false, $"No se pudo conectar: {ex.Message}");
        }
    }

    /// <summary>Crea la base si hace falta y asegura el esquema. Se llama al arrancar y al guardar la configuración.</summary>
    public async Task<DatabaseCheckResult> EnsureReadyAsync(CancellationToken ct = default)
    {
        var db = _config.Current.Database;

        try
        {
            if (db.AutoCreateDatabase)
            {
                var dbName = db.ResolveDatabaseName();
                await using var conn = new SqlConnection(db.BuildMasterConnectionString());
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = conn.CreateCommand();
                // QUOTENAME evita inyección en el identificador del catálogo.
                cmd.CommandText = @"
IF DB_ID(@name) IS NULL
BEGIN
    DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name);
    EXEC sp_executesql @sql;
END";
                cmd.Parameters.AddWithValue("@name", dbName);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var ctx = VisionDbContextFactory.Create(db.BuildConnectionString());
            await ctx.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("Base de datos lista ({Database})", db.ResolveDatabaseName());
            return new DatabaseCheckResult(true, "Base de datos lista.", null, db.ResolveDatabaseName());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo preparar la base de datos");
            return new DatabaseCheckResult(false, $"No se pudo preparar la base de datos: {ex.Message}");
        }
    }
}
