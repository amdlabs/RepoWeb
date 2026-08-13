using IVZVision.Vision.Pipeline;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace IVZVision.Web.Controllers;

/// <summary>Sirve el vídeo anotado y las imágenes de los eventos.</summary>
[ApiController]
public class StreamController : ControllerBase
{
    private const string Boundary = "ivzframe";

    private readonly FrameBroadcaster _broadcaster;
    private readonly SnapshotPathResolver _snapshots;
    private readonly ILogger<StreamController> _logger;

    public StreamController(FrameBroadcaster broadcaster, SnapshotPathResolver snapshots,
                            ILogger<StreamController> logger)
    {
        _broadcaster = broadcaster;
        _snapshots = snapshots;
        _logger = logger;
    }

    /// <summary>
    /// Flujo MJPEG con los cuadrantes ya dibujados. Se consume directamente desde
    /// un &lt;img&gt;, sin plugins ni WebRTC.
    /// </summary>
    [HttpGet("/stream/{cameraId:guid}")]
    public async Task Live(Guid cameraId, CancellationToken ct)
    {
        Response.ContentType = $"multipart/x-mixed-replace; boundary={Boundary}";
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        // El primer fotograma sale del último publicado: la imagen aparece al instante.
        var latest = _broadcaster.GetLatest(cameraId);
        if (latest is not null)
            await WritePartAsync(latest, ct).ConfigureAwait(false);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var jpeg = await _broadcaster.WaitForNextAsync(cameraId, ct).ConfigureAwait(false);
                await WritePartAsync(jpeg, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // El navegador cerró la conexión: es el final normal de un MJPEG.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Flujo MJPEG interrumpido para la cámara {CameraId}", cameraId);
        }
    }

    /// <summary>Último fotograma como JPEG suelto (útil para miniaturas y previsualizaciones).</summary>
    [HttpGet("/stream/{cameraId:guid}/instantanea")]
    public IActionResult Snapshot(Guid cameraId)
    {
        var jpeg = _broadcaster.GetLatest(cameraId);
        if (jpeg is null) return NotFound();

        Response.Headers.CacheControl = "no-store";
        return File(jpeg, "image/jpeg");
    }

    /// <summary>Sirve el recorte guardado de un evento.</summary>
    [HttpGet("/media/recorte")]
    public IActionResult Crop([FromQuery] string path)
    {
        var resolved = _snapshots.Resolve(path);
        if (resolved is null) return NotFound();

        return PhysicalFile(resolved, "image/jpeg");
    }

    private async Task WritePartAsync(byte[] jpeg, CancellationToken ct)
    {
        var header = System.Text.Encoding.ASCII.GetBytes(
            $"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");

        await Response.Body.WriteAsync(header, ct).ConfigureAwait(false);
        await Response.Body.WriteAsync(jpeg, ct).ConfigureAwait(false);
        await Response.Body.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        await Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
