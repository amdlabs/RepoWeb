using Microsoft.Data.SqlClient;

namespace IVZVision.Core.Configuration;

/// <summary>
/// Parámetros de conexión a SQL Server Express (o cualquier SQL Server).
/// Se puede indicar servidor/base/credenciales o directamente una cadena completa.
/// </summary>
public sealed class DatabaseConfig
{
    /// <summary>Instancia. Ej: <c>.\SQLEXPRESS</c>, <c>localhost\SQLEXPRESS</c>, <c>192.168.1.10,1433</c>.</summary>
    public string Server { get; set; } = @".\SQLEXPRESS";

    public string Database { get; set; } = "IVZVision";

    /// <summary>Autenticación de Windows. Si es false se usan <see cref="UserId"/> y <see cref="Password"/>.</summary>
    public bool IntegratedSecurity { get; set; } = true;

    public string UserId { get; set; } = "";

    public string Password { get; set; } = "";

    public bool TrustServerCertificate { get; set; } = true;

    public bool Encrypt { get; set; } = false;

    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>Si tiene valor, se usa tal cual y se ignora el resto de propiedades.</summary>
    public string ConnectionStringOverride { get; set; } = "";

    /// <summary>Crea la base y las tablas automáticamente al arrancar si no existen.</summary>
    public bool AutoCreateDatabase { get; set; } = true;

    public string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionStringOverride))
            return ConnectionStringOverride.Trim();

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(Server) ? @".\SQLEXPRESS" : Server.Trim(),
            InitialCatalog = string.IsNullOrWhiteSpace(Database) ? "IVZVision" : Database.Trim(),
            IntegratedSecurity = IntegratedSecurity,
            TrustServerCertificate = TrustServerCertificate,
            Encrypt = Encrypt,
            ConnectTimeout = ConnectTimeoutSeconds <= 0 ? 15 : ConnectTimeoutSeconds,
            MultipleActiveResultSets = true,
            ApplicationName = "IVZVision",
        };

        if (!IntegratedSecurity)
        {
            builder.UserID = UserId;
            builder.Password = Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>Misma conexión pero apuntando a <c>master</c>, para poder crear la base.</summary>
    public string BuildMasterConnectionString()
    {
        var builder = new SqlConnectionStringBuilder(BuildConnectionString())
        {
            InitialCatalog = "master",
        };
        return builder.ConnectionString;
    }

    /// <summary>Nombre del catálogo efectivo (respeta el override).</summary>
    public string ResolveDatabaseName()
    {
        var builder = new SqlConnectionStringBuilder(BuildConnectionString());
        return builder.InitialCatalog;
    }
}
