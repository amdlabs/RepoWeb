using IVZVision.Core.Configuration;

namespace IVZVision.Web.Services;

/// <summary>
/// Traduce la ruta relativa guardada en la base de datos a una ruta física,
/// impidiendo que un parámetro manipulado salga de la carpeta de capturas.
/// </summary>
public sealed class SnapshotPathResolver
{
    private readonly IConfigStore _config;
    private readonly IWebHostEnvironment _environment;

    public SnapshotPathResolver(IConfigStore config, IWebHostEnvironment environment)
    {
        _config = config;
        _environment = environment;
    }

    public string Root => _config.Current.Storage.Resolve(_environment.ContentRootPath);

    /// <summary>Devuelve la ruta física o null si la ruta pedida no es válida.</summary>
    public string? Resolve(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        if (Path.IsPathRooted(relativePath)) return null;
        if (relativePath.Contains("..", StringComparison.Ordinal)) return null;

        var root = Path.GetFullPath(Root);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));

        // Comprobación definitiva: la ruta resuelta tiene que colgar de la raíz.
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal)) return null;

        return File.Exists(candidate) ? candidate : null;
    }
}
