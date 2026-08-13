using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IVZVision.Web.Pages.Configuracion;

public class CamarasModel : PageModel
{
    private readonly IConfigStore _config;
    private readonly CameraPipelineManager _pipeline;

    public CamarasModel(IConfigStore config, CameraPipelineManager pipeline)
    {
        _config = config;
        _pipeline = pipeline;
    }

    public IReadOnlyList<CameraConfig> Cameras { get; private set; } = Array.Empty<CameraConfig>();

    public void OnGet() => Cameras = _config.Current.Cameras;

    public CameraStatus? StatusFor(Guid id) => _pipeline.GetStatus(id);

    public async Task<IActionResult> OnPostEliminarAsync(Guid id)
    {
        var camera = _config.Current.FindCamera(id);
        if (camera is null)
        {
            TempData["Error"] = "La cámara ya no existe.";
            return RedirectToPage();
        }

        await _config.UpdateAsync(cfg => cfg.Cameras.RemoveAll(c => c.Id == id));

        TempData["Ok"] = $"Cámara «{camera.Name}» eliminada.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAlternarAsync(Guid id)
    {
        var updated = await _config.UpdateAsync(cfg =>
        {
            var camera = cfg.FindCamera(id);
            if (camera is not null) camera.Enabled = !camera.Enabled;
        });

        var result = updated.FindCamera(id);
        TempData["Ok"] = result is null
            ? "La cámara ya no existe."
            : $"Cámara «{result.Name}» {(result.Enabled ? "habilitada" : "deshabilitada")}.";

        return RedirectToPage();
    }
}
