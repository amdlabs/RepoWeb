using IVZVision.Core.Configuration;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IVZVision.Web.Pages.Configuracion;

public class CamaraModel : PageModel
{
    private readonly IConfigStore _config;
    private readonly DiagnosticsService _diagnostics;
    private readonly ILogger<CamaraModel> _logger;

    public CamaraModel(IConfigStore config, DiagnosticsService diagnostics, ILogger<CamaraModel> logger)
    {
        _config = config;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    [BindProperty] public CameraConfig Camera { get; set; } = new();

    public bool IsNew { get; private set; }

    /// <summary>True cuando la aplicación corre dentro de un contenedor (imágenes oficiales de .NET).</summary>
    public static bool IsRunningInContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
                      StringComparison.OrdinalIgnoreCase);

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
    public async Task<IActionResult> OnPostProbarRtspAsync([FromBody] CameraConfig? camera, CancellationToken ct)
    {
        // Nunca un 500: cualquier problema vuelve como JSON con el mensaje real.
        try
        {
            if (camera is null)
                return new JsonResult(new { ok = false, mensaje = "No se recibieron los datos del formulario. Recargue la página (Ctrl+F5) y vuelva a intentarlo." });

            RestoreStoredPassword(camera);
            var result = await _diagnostics.TestRtspAsync(camera, ct);
            return new JsonResult(new { ok = result.Success, mensaje = result.Message, vistaPrevia = result.Preview });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo inesperado en la prueba de vídeo");
            return new JsonResult(new { ok = false, mensaje = $"Error inesperado: {ex.Message}" });
        }
    }

    /// <summary>Comprueba las credenciales HTTP contra el interfaz ISAPI.</summary>
    public async Task<IActionResult> OnPostProbarIsapiAsync([FromBody] CameraConfig? camera, CancellationToken ct)
    {
        try
        {
            if (camera is null)
                return new JsonResult(new { ok = false, mensaje = "No se recibieron los datos del formulario. Recargue la página (Ctrl+F5) y vuelva a intentarlo." });

            if (camera.IsUsb)
                return new JsonResult(new { ok = false, mensaje = "Una cámara USB no tiene interfaz ISAPI: esta prueba sólo aplica a cámaras de red." });

            if (string.IsNullOrWhiteSpace(camera.Host))
                return new JsonResult(new { ok = false, mensaje = "Indique la dirección IP o el nombre de la cámara antes de probar ISAPI." });

            RestoreStoredPassword(camera);
            var result = await _diagnostics.TestIsapiAsync(camera, ct);
            return new JsonResult(new { ok = result.Success, mensaje = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo inesperado en la prueba ISAPI");
            return new JsonResult(new { ok = false, mensaje = $"Error inesperado: {ex.Message}" });
        }
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
