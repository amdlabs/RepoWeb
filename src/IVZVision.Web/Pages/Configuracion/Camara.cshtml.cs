using IVZVision.Core.Configuration;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IVZVision.Web.Pages.Configuracion;

public class CamaraModel : PageModel
{
    private readonly IConfigStore _config;
    private readonly DiagnosticsService _diagnostics;

    public CamaraModel(IConfigStore config, DiagnosticsService diagnostics)
    {
        _config = config;
        _diagnostics = diagnostics;
    }

    [BindProperty] public CameraConfig Camera { get; set; } = new();

    public bool IsNew { get; private set; }

    public IActionResult OnGet(Guid? id)
    {
        if (id is null || id == Guid.Empty)
        {
            IsNew = true;
            Camera = new CameraConfig { Name = SuggestName() };
            return Page();
        }

        var existing = _config.Current.FindCamera(id.Value);
        if (existing is null)
        {
            TempData["Error"] = "La cámara solicitada no existe.";
            return RedirectToPage("/Configuracion/Camaras");
        }

        Camera = existing;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Camera.Vendor == CameraVendor.Usb)
        {
            if (Camera.UsbDeviceIndex < 0)
                ModelState.AddModelError("Camera.UsbDeviceIndex",
                    "El índice del dispositivo USB no puede ser negativo (0 = primera webcam).");
        }
        else
        {
            if (Camera.Vendor == CameraVendor.Generic && string.IsNullOrWhiteSpace(Camera.RtspUrlOverride))
                ModelState.AddModelError("Camera.RtspUrlOverride",
                    "Para una cámara genérica hay que indicar la URL RTSP completa.");

            if (string.IsNullOrWhiteSpace(Camera.Host) && string.IsNullOrWhiteSpace(Camera.RtspUrlOverride))
                ModelState.AddModelError("Camera.Host", "Indique la dirección IP o el nombre de la cámara.");
        }

        if (!ModelState.IsValid)
        {
            IsNew = _config.Current.FindCamera(Camera.Id) is null;
            return Page();
        }

        if (Camera.Id == Guid.Empty) Camera.Id = Guid.NewGuid();

        // El campo de contraseña llega vacío al editar (el navegador no la rellena):
        // en blanco significa "conservar la actual".
        RestoreStoredPassword(Camera);

        await _config.UpdateAsync(cfg =>
        {
            var index = cfg.Cameras.FindIndex(c => c.Id == Camera.Id);
            if (index >= 0) cfg.Cameras[index] = Camera;
            else cfg.Cameras.Add(Camera);
        });

        TempData["Ok"] = $"Cámara «{Camera.Name}» guardada. Se está reiniciando la captura.";
        return RedirectToPage("/Configuracion/Camaras");
    }

    /// <summary>Abre la fuente de vídeo con los datos del formulario y devuelve un fotograma de muestra.</summary>
    public async Task<IActionResult> OnPostProbarRtspAsync([FromBody] CameraConfig camera, CancellationToken ct)
    {
        RestoreStoredPassword(camera);
        var result = await _diagnostics.TestRtspAsync(camera, ct);
        return new JsonResult(new { ok = result.Success, mensaje = result.Message, vistaPrevia = result.Preview });
    }

    /// <summary>Comprueba las credenciales HTTP contra el interfaz ISAPI.</summary>
    public async Task<IActionResult> OnPostProbarIsapiAsync([FromBody] CameraConfig camera, CancellationToken ct)
    {
        RestoreStoredPassword(camera);
        var result = await _diagnostics.TestIsapiAsync(camera, ct);
        return new JsonResult(new { ok = result.Success, mensaje = result.Message });
    }

    /// <summary>Si la contraseña llegó en blanco, recupera la almacenada para esa cámara.</summary>
    private void RestoreStoredPassword(CameraConfig camera)
    {
        if (!string.IsNullOrEmpty(camera.Password)) return;

        var existing = _config.Current.FindCamera(camera.Id);
        if (existing is not null)
            camera.Password = existing.Password;
    }

    private string SuggestName()
    {
        var count = _config.Current.Cameras.Count + 1;
        return $"Cámara {count}";
    }
}
