using IVZVision.Core.Configuration;
using IVZVision.Vision.Capture;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IVZVision.Web.Pages.Configuracion;

public class CamaraModel : PageModel
{
    private readonly IConfigStore _config;
    private readonly DiagnosticsService _diagnostics;
    private readonly UsbCameraEnumerator _usb;

    public CamaraModel(IConfigStore config, DiagnosticsService diagnostics, UsbCameraEnumerator usb)
    {
        _config = config;
        _diagnostics = diagnostics;
        _usb = usb;
    }

    [BindProperty] public CameraConfig Camera { get; set; } = new();

    public bool IsNew { get; private set; }

    /// <summary>Cámaras USB encontradas en el equipo donde corre la aplicación.</summary>
    public IReadOnlyList<UsbCameraInfo> UsbCameras { get; private set; } = Array.Empty<UsbCameraInfo>();

    public IActionResult OnGet(Guid? id)
    {
        UsbCameras = _usb.Enumerate();

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
        // Los datos de red sólo se exigen a las cámaras IP.
        if (Camera.Source == CameraSource.Ip)
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
            UsbCameras = _usb.Enumerate();
            return Page();
        }

        if (Camera.Id == Guid.Empty) Camera.Id = Guid.NewGuid();

        await _config.UpdateAsync(cfg =>
        {
            var index = cfg.Cameras.FindIndex(c => c.Id == Camera.Id);
            if (index >= 0) cfg.Cameras[index] = Camera;
            else cfg.Cameras.Add(Camera);
        });

        TempData["Ok"] = $"Cámara «{Camera.Name}» guardada. Se está reiniciando la captura.";
        return RedirectToPage("/Configuracion/Camaras");
    }

    /// <summary>Abre el RTSP con los datos del formulario y devuelve un fotograma de muestra.</summary>
    public async Task<IActionResult> OnPostProbarRtspAsync([FromBody] CameraConfig camera, CancellationToken ct)
    {
        var result = await _diagnostics.TestRtspAsync(camera, ct);
        return new JsonResult(new { ok = result.Success, mensaje = result.Message, vistaPrevia = result.Preview });
    }

    /// <summary>Comprueba las credenciales HTTP contra el interfaz ISAPI.</summary>
    public async Task<IActionResult> OnPostProbarIsapiAsync([FromBody] CameraConfig camera, CancellationToken ct)
    {
        var result = await _diagnostics.TestIsapiAsync(camera, ct);
        return new JsonResult(new { ok = result.Success, mensaje = result.Message });
    }

    /// <summary>Vuelve a buscar cámaras USB en el equipo (probando también los dispositivos).</summary>
    public IActionResult OnPostBuscarUsb()
    {
        var found = _usb.Enumerate(probeDevices: true);

        return new JsonResult(new
        {
            ok = found.Count > 0,
            mensaje = found.Count == 0
                ? "No se ha encontrado ninguna cámara USB. En Linux compruebe que existe /dev/video* y que el " +
                  "proceso pertenece al grupo «video»; en un contenedor hay que pasar el dispositivo con --device."
                : string.Join('\n', found.Select(c => (c.Available ? "OK  · " : "??  · ") + c.Display)),
            dispositivos = found.Select(c => new { c.Index, c.DevicePath, c.Name, c.Available }),
        });
    }

    private string SuggestName()
    {
        var count = _config.Current.Cameras.Count + 1;
        return $"Cámara {count}";
    }
}
