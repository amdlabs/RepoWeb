using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace IVZVision.Vision.Capture;

/// <summary>Cámara USB encontrada en el equipo donde corre la aplicación.</summary>
public sealed record UsbCameraInfo(int Index, string? DevicePath, string Name, bool Available)
{
    public string Display => string.IsNullOrEmpty(DevicePath)
        ? $"[{Index}] {Name}"
        : $"[{Index}] {Name} ({DevicePath})";
}

/// <summary>
/// Enumera las cámaras USB del equipo. En Linux se leen de <c>/sys</c>, que da el
/// nombre real del dispositivo sin abrirlo; en Windows y macOS no hay una vía
/// equivalente desde OpenCV, así que se prueban los primeros índices.
/// </summary>
public sealed class UsbCameraEnumerator
{
    private const int MaxProbedIndex = 8;

    private readonly ILogger<UsbCameraEnumerator> _logger;

    public UsbCameraEnumerator(ILogger<UsbCameraEnumerator> logger) => _logger = logger;

    public IReadOnlyList<UsbCameraInfo> Enumerate(bool probeDevices = false)
    {
        try
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? EnumerateLinux(probeDevices)
                : ProbeIndices();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron enumerar las cámaras USB");
            return Array.Empty<UsbCameraInfo>();
        }
    }

    private IReadOnlyList<UsbCameraInfo> EnumerateLinux(bool probeDevices)
    {
        const string v4lRoot = "/sys/class/video4linux";
        var found = new List<UsbCameraInfo>();

        if (!Directory.Exists(v4lRoot))
        {
            _logger.LogInformation("No hay dispositivos V4L2 en este equipo (¿falta pasarlos al contenedor?)");
            return found;
        }

        foreach (var dir in Directory.EnumerateDirectories(v4lRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var node = Path.GetFileName(dir);                 // video0, video1…
            if (!node.StartsWith("video", StringComparison.Ordinal)) continue;
            if (!int.TryParse(node.AsSpan(5), out var index)) continue;

            var name = ReadOrDefault(Path.Combine(dir, "name"), node);
            var devicePath = $"/dev/{node}";

            // Una webcam expone varios nodos; sólo el de captura entrega vídeo.
            if (!SupportsCapture(dir)) continue;

            var available = !probeDevices || CanOpen(devicePath, index);
            found.Add(new UsbCameraInfo(index, devicePath, name, available));
        }

        return found;
    }

    /// <summary>
    /// Filtra los nodos que no son de captura (los de metadatos o de salida).
    /// El fichero index de /sys no lo dice, pero device_caps sí.
    /// </summary>
    private static bool SupportsCapture(string sysDir)
    {
        const uint videoCapture = 0x00000001;

        var raw = ReadOrDefault(Path.Combine(sysDir, "device_caps"), "");
        if (string.IsNullOrWhiteSpace(raw)) return true;   // sin dato: no se descarta

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = raw[2..];

        return !uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out var caps)
               || (caps & videoCapture) != 0;
    }

    private IReadOnlyList<UsbCameraInfo> ProbeIndices()
    {
        var found = new List<UsbCameraInfo>();

        // Se para en el primer hueco: los índices los asigna el sistema de forma
        // consecutiva y seguir probando sólo añade segundos de espera.
        for (var index = 0; index < MaxProbedIndex; index++)
        {
            if (!CanOpen(null, index)) break;
            found.Add(new UsbCameraInfo(index, null, $"Cámara {index}", true));
        }

        return found;
    }

    private bool CanOpen(string? devicePath, int index)
    {
        try
        {
            using var capture = devicePath is not null && RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? new VideoCapture(devicePath, CameraSourceFactory.UsbBackend)
                : new VideoCapture(index, CameraSourceFactory.UsbBackend);

            return capture.IsOpened();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo abrir el dispositivo {Index}", index);
            return false;
        }
    }

    private static string ReadOrDefault(string path, string fallback)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
