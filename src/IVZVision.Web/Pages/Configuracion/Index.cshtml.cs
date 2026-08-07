using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Vision.Engine;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace IVZVision.Web.Pages.Configuracion;

public class IndexModel : PageModel
{
    private readonly IConfigStore _config;
    private readonly DiagnosticsService _diagnostics;
    private readonly RecognitionEngine _engine;
    private readonly KnownSubjectsIndex _index;

    public IndexModel(IConfigStore config, DiagnosticsService diagnostics,
                      RecognitionEngine engine, KnownSubjectsIndex index)
    {
        _config = config;
        _diagnostics = diagnostics;
        _engine = engine;
        _index = index;
    }

    [BindProperty] public DatabaseConfig Database { get; set; } = new();
    [BindProperty] public ModelsConfig Models { get; set; } = new();
    [BindProperty] public RecognitionConfig Recognition { get; set; } = new();
    [BindProperty] public StorageConfig Storage { get; set; } = new();
    [BindProperty] public SecurityConfig Security { get; set; } = new();

    public ModelStatus ModelStatus { get; private set; } = new();
    public string SettingsPath => _config.FilePath;

    /// <summary>True si la cadena de conexión viene fijada por web.config / appsettings.</summary>
    public bool ConnectionForced => (_config as DbConfigStore)?.ConnectionStringIsForced ?? false;

    /// <summary>Resumen de la última comprobación de actualizaciones de modelos.</summary>
    public string ModelUpdatesInfo
    {
        get
        {
            var updates = HttpContext.RequestServices.GetRequiredService<ModelUpdateService>();
            return updates.LastCheckAt is null
                ? "todavía no se ha ejecutado"
                : $"{updates.LastCheckAt.Value.ToLocalTime():dd/MM HH:mm} · {updates.LastResult}";
        }
    }

    /// <summary>Ficheros ONNX disponibles en la carpeta de modelos, para las listas desplegables.</summary>
    public IReadOnlyList<string> OnnxFiles { get; private set; } = Array.Empty<string>();

    /// <summary>Ficheros de texto (diccionarios y listas de clases) de la carpeta de modelos.</summary>
    public IReadOnlyList<string> TextFiles { get; private set; } = Array.Empty<string>();

    /// <summary>Opciones para un desplegable de modelos, incluyendo el valor actual aunque el fichero falte.</summary>
    public List<SelectListItem> ModelOptions(string current, bool onnx)
    {
        var files = (onnx ? OnnxFiles : TextFiles).ToList();
        if (!string.IsNullOrWhiteSpace(current) && !files.Contains(current, StringComparer.OrdinalIgnoreCase))
            files.Insert(0, current);

        return files.Select(f => new SelectListItem(f, f)).ToList();
    }

    private void ScanModelFiles()
    {
        try
        {
            var dir = _config.Current.Models.Resolve(".", HttpContext.RequestServices
                .GetRequiredService<IWebHostEnvironment>().ContentRootPath);

            if (Directory.Exists(dir))
            {
                OnnxFiles = Directory.EnumerateFiles(dir, "*.onnx")
                    .Select(Path.GetFileName)
                    .Where(f => f is not null)
                    .Select(f => f!)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                TextFiles = Directory.EnumerateFiles(dir, "*.txt")
                    .Select(Path.GetFileName)
                    .Where(f => f is not null)
                    .Select(f => f!)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception)
        {
            // Sin listado no hay desplegable, pero el campo sigue mostrando el valor actual.
        }
    }
    public int KnownFaces => _index.FaceTemplateCount;
    public int KnownPlates => _index.PlateCount;
    public string IndexRefreshed => _index.LastRefreshedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "nunca";

    public void OnGet()
    {
        var current = _config.Current;
        Database = current.Database;
        Models = current.Models;
        Recognition = current.Recognition;
        Storage = current.Storage;
        Security = current.Security;
        ModelStatus = _engine.Status;
        ScanModelFiles();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            ModelStatus = _engine.Status;
            ScanModelFiles();
            return Page();
        }

        // Contraseña en blanco al guardar = conservar la almacenada (el navegador
        // nunca rellena los campos de tipo password).
        if (string.IsNullOrEmpty(Database.Password))
            Database.Password = _config.Current.Database.Password;

        await _config.UpdateAsync(cfg =>
        {
            cfg.Database = Database;
            cfg.Models = Models;
            cfg.Recognition = Recognition;
            cfg.Storage = Storage;
            cfg.Security = Security;
        });

        TempData["Ok"] = "Configuración guardada. Los modelos y las cámaras se están reiniciando con los nuevos valores.";
        return RedirectToPage();
    }

    /// <summary>Prueba la conexión con SQL Server sin guardar nada.</summary>
    public async Task<IActionResult> OnPostProbarBdAsync([FromBody] DatabaseConfig? database, CancellationToken ct)
    {
        try
        {
            if (database is null)
                return new JsonResult(new { ok = false, mensaje = "No se recibieron los datos del formulario. Recargue la página (Ctrl+F5) y vuelva a intentarlo." });

            if (string.IsNullOrEmpty(database.Password))
                database.Password = _config.Current.Database.Password;

            var result = await DatabaseProvisioner.TestAsync(database, ct);
            return new JsonResult(new { ok = result.Success, mensaje = result.Message, version = result.ServerVersion });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, mensaje = $"Error inesperado: {ex.Message}" });
        }
    }

    /// <summary>Comprueba que los modelos ONNX existen y se pueden cargar.</summary>
    public IActionResult OnPostVerificarModelos([FromBody] ModelsConfig? models)
    {
        try
        {
            if (models is null)
                return new JsonResult(new { ok = false, mensaje = "No se recibieron los datos del formulario. Recargue la página (Ctrl+F5) y vuelva a intentarlo." });

            var result = _diagnostics.CheckModels(models);
            return new JsonResult(new { ok = result.Success, mensaje = result.Message });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, mensaje = $"Error inesperado: {ex.Message}" });
        }
    }
}
