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

            // EnsureCreated no toca bases ya existentes: los elementos añadidos en
            // versiones posteriores se aplican aquí de forma idempotente.
            await ctx.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('RecognitionEvents', 'ObjectClass') IS NULL
    ALTER TABLE RecognitionEvents ADD ObjectClass nvarchar(60) NULL;

IF COL_LENGTH('RecognitionEvents', 'CropBase64') IS NULL
    ALTER TABLE RecognitionEvents ADD CropBase64 nvarchar(max) NULL;

IF OBJECT_ID('ObjectLabels', 'U') IS NULL
BEGIN
    CREATE TABLE ObjectLabels (
        Id           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ObjectLabels PRIMARY KEY,
        ClassName    nvarchar(60)  NOT NULL,
        DisplayName  nvarchar(150) NOT NULL,
        Notes        nvarchar(400) NULL,
        IsAuthorized bit NOT NULL,
        IsActive     bit NOT NULL,
        CreatedAt    datetime2 NOT NULL
    );
    CREATE UNIQUE INDEX IX_ObjectLabels_ClassName ON ObjectLabels (ClassName);
END

IF OBJECT_ID('SystemUsers', 'U') IS NULL
BEGIN
    CREATE TABLE SystemUsers (
        Id           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SystemUsers PRIMARY KEY,
        Username     nvarchar(100) NOT NULL,
        FullName     nvarchar(200) NOT NULL,
        PasswordHash nvarchar(500) NOT NULL,
        Role         int NOT NULL,
        IsActive     bit NOT NULL,
        CreatedAt    datetime2 NOT NULL
    );
    CREATE UNIQUE INDEX IX_SystemUsers_Username ON SystemUsers (Username);
END

IF OBJECT_ID('ConfigSnapshots', 'U') IS NULL
BEGIN
    CREATE TABLE ConfigSnapshots (
        Id      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConfigSnapshots PRIMARY KEY,
        SavedAt datetime2 NOT NULL,
        Json    nvarchar(max) NOT NULL
    );
    CREATE INDEX IX_ConfigSnapshots_SavedAt ON ConfigSnapshots (SavedAt);
END

IF OBJECT_ID('AppConfiguration', 'U') IS NULL
BEGIN
    CREATE TABLE AppConfiguration (
        Id        int NOT NULL CONSTRAINT PK_AppConfiguration PRIMARY KEY,
        Json      nvarchar(max) NOT NULL,
        UpdatedAt datetime2 NOT NULL
    );
END

IF OBJECT_ID('PlateCorrections', 'U') IS NULL
BEGIN
    CREATE TABLE PlateCorrections (
        Id           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PlateCorrections PRIMARY KEY,
        WrongText    nvarchar(20) NOT NULL,
        CorrectText  nvarchar(20) NOT NULL,
        TimesApplied int NOT NULL,
        CorrectedBy  nvarchar(150) NULL,
        CreatedAt    datetime2 NOT NULL
    );
    CREATE UNIQUE INDEX IX_PlateCorrections_WrongText ON PlateCorrections (WrongText);
END", ct).ConfigureAwait(false);

            // Usuario administrador inicial para que el sistema sea usable nada más instalar.
            if (!await ctx.SystemUsers.AnyAsync(ct).ConfigureAwait(false))
            {
                ctx.SystemUsers.Add(new Entities.SystemUser
                {
                    Username = "admin",
                    FullName = "Administrador",
                    PasswordHash = Entities.SystemUser.HashPassword("admin"),
                    Role = Entities.SystemUserRole.Administrator,
                });
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Creado el usuario inicial «admin» con contraseña «admin»: cámbiela cuanto antes.");
            }

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
