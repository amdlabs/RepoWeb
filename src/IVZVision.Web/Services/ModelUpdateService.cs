using System.Text.Json;
using IVZVision.Core.Configuration;
using IVZVision.Vision.Engine;

namespace IVZVision.Web.Services;

/// <summary>
/// Mantiene los modelos ONNX al día: comprueba periódicamente las fuentes
/// oficiales (por ETag y tamaño), descarga las versiones nuevas de forma atómica
/// y recarga el motor de reconocimiento sin reiniciar la aplicación.
/// </summary>
public sealed class ModelUpdateService : BackgroundService
{
    /// <summary>Catálogo de modelos gestionados: fichero local → fuente oficial.</summary>
    private static readonly IReadOnlyDictionary<string, string> Catalog = new Dictionary<string, string>
    {
        ["face_detection_yunet_2023mar.onnx"] =
            "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx",
        ["face_recognition_sface_2021dec.onnx"] =
            "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx",
        ["yolov5s.onnx"] =
            "https://github.com/ultralytics/yolov5/releases/download/v7.0/yolov5s.onnx",
        ["plate_ocr_rec.onnx"] =
            "https://huggingface.co/SWHL/RapidOCR/resolve/main/PP-OCRv3/en_PP-OCRv3_rec_infer.onnx",
        ["plate_ocr_charset_en.txt"] =
            "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/ppocr/utils/en_dict.txt",
        ["license_plate_detector.onnx"] =
            "https://huggingface.co/morsetechlab/yolov11-license-plate-detection/resolve/main/license-plate-finetune-v1n.onnx",
        ["text_detector.onnx"] =
            "https://huggingface.co/SWHL/RapidOCR/resolve/main/PP-OCRv3/ch_PP-OCRv3_det_infer.onnx",
    };

    private readonly IConfigStore _config;
    private readonly RecognitionEngine _engine;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ModelUpdateService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public DateTimeOffset? LastCheckAt { get; private set; }
    public string? LastResult { get; private set; }

    public ModelUpdateService(IConfigStore config, RecognitionEngine engine,
                              IWebHostEnvironment environment, ILogger<ModelUpdateService> logger)
    {
        _config = config;
        _engine = engine;
        _environment = environment;
        _logger = logger;
    }

    private string StateFile => Path.Combine(_environment.ContentRootPath, "App_Data", "modelos-versiones.json");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Primer chequeo poco después de arrancar; luego, según el intervalo configurado.
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var models = _config.Current.Models;

            if (models.AutoUpdateModels)
            {
                try { await CheckAndUpdateAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    LastResult = $"Error: {ex.Message}";
                    _logger.LogWarning(ex, "La comprobación de actualizaciones de modelos falló");
                }
            }

            var hours = Math.Clamp(_config.Current.Models.AutoUpdateIntervalHours, 1, 24 * 30);
            try { await Task.Delay(TimeSpan.FromHours(hours), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAndUpdateAsync(CancellationToken ct)
    {
        var modelsDir = _config.Current.Models.Resolve(".", _environment.ContentRootPath);
        var state = LoadState();
        var updated = new List<string>();
        var checkedCount = 0;

        foreach (var (fileName, url) in Catalog)
        {
            ct.ThrowIfCancellationRequested();

            var localPath = Path.Combine(modelsDir, fileName);
            if (!File.Exists(localPath)) continue; // sólo se actualiza lo ya instalado

            checkedCount++;

            string? etag = null;
            long remoteSize = -1;
            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await _http.SendAsync(head, ct);
                if (!response.IsSuccessStatusCode) continue;

                etag = response.Headers.ETag?.Tag;
                remoteSize = response.Content.Headers.ContentLength ?? -1;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo comprobar {File}", fileName);
                continue;
            }

            var localSize = new FileInfo(localPath).Length;
            state.TryGetValue(fileName, out var known);

            var isNew = etag is not null
                ? !string.Equals(known?.ETag, etag, StringComparison.Ordinal)
                : (remoteSize > 0 && remoteSize != localSize);

            // Primera ejecución: si el tamaño local coincide con el remoto, se anota la
            // versión actual sin descargar nada.
            if (isNew && known is null && remoteSize > 0 && remoteSize == localSize)
            {
                state[fileName] = new ModelVersion(etag, remoteSize, DateTimeOffset.UtcNow);
                continue;
            }

            if (!isNew)
            {
                state[fileName] = new ModelVersion(etag ?? known?.ETag, remoteSize, DateTimeOffset.UtcNow);
                continue;
            }

            // Hay versión nueva: descarga a un temporal y sustitución atómica.
            try
            {
                var tmp = localPath + ".nuevo";
                await using (var output = File.Create(tmp))
                await using (var input = await _http.GetStreamAsync(url, ct))
                    await input.CopyToAsync(output, ct);

                var downloaded = new FileInfo(tmp).Length;
                if (downloaded == 0 || (remoteSize > 0 && downloaded != remoteSize))
                {
                    File.Delete(tmp);
                    _logger.LogWarning("Descarga incompleta de {File}; se conserva la versión actual", fileName);
                    continue;
                }

                // El motor suelta sus sesiones para liberar el fichero antes de sustituirlo.
                _engine.Reload();
                await ReplaceWithRetryAsync(tmp, localPath, ct);

                state[fileName] = new ModelVersion(etag, downloaded, DateTimeOffset.UtcNow);
                updated.Add(fileName);
                _logger.LogInformation("Modelo actualizado: {File} ({Size:N0} bytes)", fileName, downloaded);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo actualizar {File}; se conserva la versión actual", fileName);
            }
        }

        SaveState(state);
        LastCheckAt = DateTimeOffset.UtcNow;
        LastResult = updated.Count > 0
            ? $"Actualizados: {string.Join(", ", updated)}"
            : $"Sin novedades ({checkedCount} modelos comprobados)";

        if (updated.Count > 0)
        {
            _engine.Reload();
            _engine.EnsureLoaded();
        }

        _logger.LogInformation("Comprobación de actualizaciones de modelos: {Result}", LastResult);
    }

    private static async Task ReplaceWithRetryAsync(string source, string destination, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                // El fichero puede seguir abierto un instante por una inferencia en curso.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private sealed record ModelVersion(string? ETag, long Size, DateTimeOffset CheckedAt);

    private Dictionary<string, ModelVersion> LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<Dictionary<string, ModelVersion>>(File.ReadAllText(StateFile))
                       ?? new Dictionary<string, ModelVersion>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo leer el estado de versiones de modelos");
        }

        return new Dictionary<string, ModelVersion>();
    }

    private void SaveState(Dictionary<string, ModelVersion> state)
    {
        try
        {
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo guardar el estado de versiones de modelos");
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
