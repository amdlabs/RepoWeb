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
    [BindProperty] public ActivityConfig Activity { get; set; } = new();
    [BindProperty] public ApiConfig Api { get; set; } = new();

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
        Activity = current.Activity;
        Api = current.Api;
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
            cfg.Activity = Activity;

            // Los tokens se administran con sus propios botones: el formulario general
            // no debe borrarlos ni reescribir sus valores.
            cfg.Api.RestEnabled = Api.RestEnabled;
            cfg.Api.McpEnabled = Api.McpEnabled;
            cfg.Api.McpRequiresToken = Api.McpRequiresToken;
            cfg.Api.RequestsPerMinute = Api.RequestsPerMinute;
        });

        TempData["Ok"] = "Configuración guardada. Los modelos y las cámaras se están reiniciando con los nuevos valores.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCrearTokenAsync(string nombre)
    {
        var token = new ApiToken
        {
            Name = string.IsNullOrWhiteSpace(nombre) ? "Integración" : nombre.Trim(),
            Value = ApiToken.GenerateValue(),
        };

        await _config.UpdateAsync(cfg => cfg.Api.Tokens.Add(token));

        // Es la única vez que se muestra completo: después sólo se ven los últimos caracteres.
        TempData["Ok"] = $"Token «{token.Name}» creado. Cópielo ahora, no se volverá a mostrar entero: {token.Value}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBorrarTokenAsync(string id)
    {
        await _config.UpdateAsync(cfg => cfg.Api.Tokens.RemoveAll(t => t.Id == id));
        TempData["Ok"] = "Token eliminado.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAlternarTokenAsync(string id)
    {
        await _config.UpdateAsync(cfg =>
        {
            var token = cfg.Api.Tokens.FirstOrDefault(t => t.Id == id);
            if (token is not null) token.Enabled = !token.Enabled;
        });

        TempData["Ok"] = "Token actualizado.";
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
