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

    /// <summary>
    /// Borra los registros capturados: eventos de reconocimiento y sus imágenes en disco.
    /// No toca el padrón (personas, vehículos, objetos etiquetados ni usuarios).
    /// </summary>
    public async Task<IActionResult> OnPostLimpiarRegistrosAsync(int? dias, string? confirmacion,
                                                                 CancellationToken ct)
    {
        // Confirmación explícita escrita por el usuario: evita borrados accidentales.
        if (!string.Equals(confirmacion?.Trim(), "BORRAR", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Para vaciar los registros escriba BORRAR en el campo de confirmación.";
            return RedirectToPage();
        }

        try
        {
            var dbFactory = HttpContext.RequestServices
                .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<VisionDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var cutoff = dias is > 0 ? DateTime.UtcNow.AddDays(-dias.Value) : (DateTime?)null;

            var query = db.RecognitionEvents.AsQueryable();
            if (cutoff.HasValue) query = query.Where(e => e.OccurredAt < cutoff.Value);

            // Primero las imágenes en disco de los eventos que se van a borrar.
            var paths = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                query.Where(e => e.CropPath != null).Select(e => e.CropPath!), ct);

            var root = _config.Current.Storage.Resolve(
                HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().ContentRootPath);

            var borradas = 0;
            foreach (var path in paths)
            {
                try
                {
                    var full = Path.Combine(root, path);
                    if (System.IO.File.Exists(full)) { System.IO.File.Delete(full); borradas++; }
                }
                catch (Exception) { /* un fichero bloqueado no debe abortar la limpieza */ }
            }

            var eliminados = await Microsoft.EntityFrameworkCore.RelationalQueryableExtensions
                .ExecuteDeleteAsync(query, ct);

            TempData["Ok"] = cutoff.HasValue
                ? $"Se han borrado {eliminados} registro(s) anteriores a {dias} día(s) y {borradas} imagen(es)."
                : $"Se han borrado todos los registros capturados ({eliminados}) y {borradas} imagen(es). " +
                  "El padrón de personas, vehículos y objetos se mantiene intacto.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"No se pudieron borrar los registros: {ex.Message}";
        }

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

    /// <summary>
    /// Aplica los umbrales del formulario (sin guardarlos) sobre el último fotograma
    /// de la primera cámara conectada y devuelve la imagen anotada.
    /// </summary>
    public IActionResult OnPostProbarUmbrales([FromBody] RecognitionConfig? recognition)
    {
        try
        {
            if (recognition is null)
                return new JsonResult(new { ok = false, mensaje = "No se recibieron los datos del formulario. Recargue la página (Ctrl+F5) y vuelva a intentarlo." });

            var broadcaster = HttpContext.RequestServices.GetRequiredService<IVZVision.Vision.Pipeline.FrameBroadcaster>();

            byte[]? jpeg = null;
            string? cameraName = null;
            foreach (var camera in _config.Current.Cameras.Where(c => c.Enabled))
            {
                jpeg = broadcaster.GetLatest(camera.Id);
                if (jpeg is not null) { cameraName = camera.Name; break; }
            }

            if (jpeg is null)
                return new JsonResult(new { ok = false, mensaje = "Ninguna cámara tiene todavía un fotograma disponible." });

            using var frame = OpenCvSharp.Cv2.ImDecode(jpeg, OpenCvSharp.ImreadModes.Color);
            if (frame.Empty())
                return new JsonResult(new { ok = false, mensaje = "No se pudo decodificar el fotograma de prueba." });

            var testCamera = new CameraConfig
            {
                Name = cameraName ?? "prueba",
                EnableFaceRecognition = true,
                EnablePlateRecognition = true,
                EnableObjectDetection = true,
                EnableTextReading = true,
            };

            var items = _engine.Analyze(frame, testCamera, recognition);

            var observations = items.Select(i => new IVZVision.Core.Detection.Observation
            {
                Kind = i.Kind,
                Box = i.Box,
                DetectionScore = i.Score,
                PlateText = i.PlateText,
                OcrConfidence = i.OcrConfidence,
                ObjectClass = i.ObjectClass,
                Annotation = i.Annotation,
                Match = i.Match,
            }).ToList();

            IVZVision.Vision.Drawing.Annotator.Draw(frame, observations);

            var annotated = IVZVision.Vision.Imaging.ImageOps.EncodeJpeg(frame, 82);
            var counts = observations.GroupBy(o => o.Kind)
                .Select(g => $"{g.Count()} {g.Key switch
                {
                    IVZVision.Core.Detection.ObservationKind.Face => "rostro(s)",
                    IVZVision.Core.Detection.ObservationKind.Plate => "matrícula(s)",
                    IVZVision.Core.Detection.ObservationKind.Object => "objeto(s)",
                    _ => "texto(s)",
                }}");

            return new JsonResult(new
            {
                ok = true,
                mensaje = $"Cámara «{cameraName}»: {(observations.Count == 0 ? "sin detecciones con estos umbrales" : string.Join(", ", counts))}.",
                vistaPrevia = $"data:image/jpeg;base64,{Convert.ToBase64String(annotated)}",
            });
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
