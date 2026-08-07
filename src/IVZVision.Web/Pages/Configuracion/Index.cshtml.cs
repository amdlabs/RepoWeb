using IVZVision.Core.Configuration;
using IVZVision.Data;
using IVZVision.Vision.Engine;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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

    public ModelStatus ModelStatus { get; private set; } = new();
    public string SettingsPath => _config.FilePath;
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
        ModelStatus = _engine.Status;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            ModelStatus = _engine.Status;
            return Page();
        }

        await _config.UpdateAsync(cfg =>
        {
            cfg.Database = Database;
            cfg.Models = Models;
            cfg.Recognition = Recognition;
            cfg.Storage = Storage;
        });

        TempData["Ok"] = "Configuración guardada. Los modelos y las cámaras se están reiniciando con los nuevos valores.";
        return RedirectToPage();
    }

    /// <summary>Prueba la conexión con SQL Server sin guardar nada.</summary>
    public async Task<IActionResult> OnPostProbarBdAsync([FromBody] DatabaseConfig database, CancellationToken ct)
    {
        var result = await DatabaseProvisioner.TestAsync(database, ct);
        return new JsonResult(new { ok = result.Success, mensaje = result.Message, version = result.ServerVersion });
    }

    /// <summary>Comprueba que los modelos ONNX existen y se pueden cargar.</summary>
    public IActionResult OnPostVerificarModelos([FromBody] ModelsConfig models)
    {
        var result = _diagnostics.CheckModels(models);
        return new JsonResult(new { ok = result.Success, mensaje = result.Message });
    }
}
