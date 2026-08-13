using IVZVision.Core.Configuration;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Data;

/// <summary>
/// Crea contextos usando siempre la cadena de conexión que hay en la configuración
/// en ese momento: al cambiarla desde la web no hace falta reiniciar la aplicación.
/// </summary>
public sealed class VisionDbContextFactory : IDbContextFactory<VisionDbContext>
{
    private readonly IConfigStore _config;

    public VisionDbContextFactory(IConfigStore config) => _config = config;

    public VisionDbContext CreateDbContext() => Create(_config.Current.Database.BuildConnectionString());

    public static VisionDbContext Create(string connectionString)
    {
        var options = new DbContextOptionsBuilder<VisionDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
                sql.CommandTimeout(60);
            })
            .Options;

        return new VisionDbContext(options);
    }
}
