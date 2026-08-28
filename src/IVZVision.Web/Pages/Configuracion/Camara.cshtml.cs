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

    /// <summary>Zonas dibujadas en el editor visual, serializadas en JSON.</summary>
    [BindProperty] public string? ZonasJson { get; set; }

    /// <summary>Zonas de la cámara actual para pintarlas al abrir el editor.</summary>
    public string ZonasParaEditor => System.Text.Json.JsonSerializer.Serialize(
        Camera.Zones, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });

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

        // Zonas dibujadas en el editor visual: llegan como JSON en un campo oculto.
        if (ZonasJson is not null)
        {
            try
            {
                Camera.Zones = System.Text.Json.JsonSerializer.Deserialize<List<DetectionZone>>(
                    ZonasJson, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    }) ?? new List<DetectionZone>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron leer las zonas de detección; se conservan las anteriores");
                var previa = _config.Current.FindCamera(Camera.Id);
                if (previa is not null) Camera.Zones = previa.Zones;
            }
        }

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

    /// <summary>Consulta al DVR/NVR los canales de vídeo que publica.</summary>
    public async Task<IActionResult> OnPostBuscarCanalesAsync([FromBody] CameraConfig? camera, CancellationToken ct)
    {
        try
        {
            if (camera is null)
                return new JsonResult(new { ok = false, mensaje = "No se recibieron los datos del formulario. Recargue la página (Ctrl+F5) y vuelva a intentarlo." });

            if (camera.IsUsb)
                return new JsonResult(new { ok = false, mensaje = "Una cámara USB no publica canales: esta función es para DVR/NVR de red." });

            if (string.IsNullOrWhiteSpace(camera.Host))
                return new JsonResult(new { ok = false, mensaje = "Indique la dirección IP o el nombre del DVR/NVR." });

            RestoreStoredPassword(camera);

            using var client = new IVZVision.Vision.Isapi.HikvisionIsapiClient(camera, _logger);
            var result = await client.ListChannelsAsync(ct);

            return new JsonResult(new
            {
                ok = result.Success,
                mensaje = result.Message,
                canales = result.Channels.Select(c => new { canal = c.Channel, nombre = c.Name, enLinea = c.Online }),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo inesperado al buscar canales del DVR");
            return new JsonResult(new { ok = false, mensaje = $"Error inesperado: {ex.Message}" });
        }
    }

    public sealed class CanalSeleccionado
    {
        public int Canal { get; set; }
        public string? Nombre { get; set; }
    }

    public sealed class AgregarCanalesRequest
    {
        public CameraConfig Camera { get; set; } = new();
        public List<CanalSeleccionado> Canales { get; set; } = new();
    }

    /// <summary>Da de alta una cámara por cada canal del DVR seleccionado, con la misma conexión.</summary>
    public async Task<IActionResult> OnPostAgregarCanalesAsync([FromBody] AgregarCanalesRequest? request, CancellationToken ct)
    {
        try
        {
            if (request is null || request.Canales.Count == 0)
                return new JsonResult(new { ok = false, mensaje = "Seleccione al menos un canal." });

            RestoreStoredPassword(request.Camera);

            var dvrName = string.IsNullOrWhiteSpace(request.Camera.Name) ? request.Camera.Host : request.Camera.Name.Trim();

            await _config.UpdateAsync(cfg =>
            {
                foreach (var canal in request.Canales)
                {
                    // Un canal ya dado de alta del mismo equipo no se duplica.
                    if (cfg.Cameras.Any(c => !c.IsUsb
                                             && c.Host.Equals(request.Camera.Host, StringComparison.OrdinalIgnoreCase)
                                             && c.Channel == canal.Canal))
                        continue;

                    var json = System.Text.Json.JsonSerializer.Serialize(request.Camera, ConfigJson.Options);
                    var copy = System.Text.Json.JsonSerializer.Deserialize<CameraConfig>(json, ConfigJson.Options)!;

                    copy.Id = Guid.NewGuid();
                    copy.Channel = canal.Canal;
                    copy.RtspUrlOverride = "";
                    copy.Name = string.IsNullOrWhiteSpace(canal.Nombre)
                        ? $"{dvrName} · canal {canal.Canal}"
                        : $"{dvrName} · {canal.Nombre!.Trim()}";

                    cfg.Cameras.Add(copy);
                }
            }, ct);

            return new JsonResult(new
            {
                ok = true,
                mensaje = $"Se han añadido {request.Canales.Count} cámara(s) del DVR. La captura se está reiniciando.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo inesperado al añadir canales del DVR");
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
