using System.ComponentModel.DataAnnotations;
using System.Text;

namespace IVZVision.Core.Configuration;

public enum CameraVendor
{
    /// <summary>Hikvision y OEM compatibles (Hilook, Safire, LTS…).</summary>
    [Display(Name = "Hikvision / OEM")]
    Hikvision = 0,

    [Display(Name = "Dahua")]
    Dahua = 1,

    /// <summary>Cualquier cámara ONVIF/RTSP: se usa la URL indicada a mano.</summary>
    [Display(Name = "Genérica (RTSP/ONVIF)")]
    Generic = 2,

    /// <summary>Cámara USB o integrada del propio equipo (webcam, capturadora…).</summary>
    [Display(Name = "USB / local")]
    Usb = 3,
}

public enum StreamProfile
{
    Main = 1,
    Sub = 2,
    Third = 3,
}

/// <summary>Parámetros de una cámara IP y de lo que se debe reconocer en ella.</summary>
public sealed class CameraConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Cámara 1";

    public bool Enabled { get; set; } = true;

    public CameraVendor Vendor { get; set; } = CameraVendor.Hikvision;

    public string Host { get; set; } = "192.168.1.64";

    public int RtspPort { get; set; } = 554;

    /// <summary>Puerto HTTP del interfaz ISAPI (por defecto 80).</summary>
    public int HttpPort { get; set; } = 80;

    public bool UseHttps { get; set; } = false;

    public string Username { get; set; } = "admin";

    public string Password { get; set; } = "";

    /// <summary>Canal del NVR/cámara (1 en cámaras standalone).</summary>
    public int Channel { get; set; } = 1;

    /// <summary>Índice del dispositivo local para <see cref="CameraVendor.Usb"/> (0 = primera webcam).</summary>
    public int UsbDeviceIndex { get; set; } = 0;

    /// <summary>True cuando la fuente es un dispositivo local y no un flujo de red.</summary>
    public bool IsUsb => Vendor == CameraVendor.Usb;

    public StreamProfile Stream { get; set; } = StreamProfile.Sub;

    /// <summary>Si tiene valor se usa como URL RTSP tal cual (obligatorio para <see cref="CameraVendor.Generic"/>).</summary>
    public string RtspUrlOverride { get; set; } = "";

    /// <summary>Fuerza transporte RTSP sobre TCP (recomendado: evita frames rotos).</summary>
    public bool UseTcpTransport { get; set; } = true;

    /// <summary>Fotogramas por segundo que se envían al motor de reconocimiento (no afecta al vídeo mostrado).</summary>
    public double AnalysisFps { get; set; } = 3;

    public bool EnableFaceRecognition { get; set; } = true;

    public bool EnablePlateRecognition { get; set; } = true;

    /// <summary>Detección genérica de objetos (personas, vehículos, animales…) con el modelo COCO local.</summary>
    public bool EnableObjectDetection { get; set; } = true;

    /// <summary>Lee los textos visibles en la escena (carteles, rótulos…) con OCR local.</summary>
    public bool EnableTextReading { get; set; } = true;

    /// <summary>Escucha los eventos ANPR nativos de la cámara vía ISAPI, además del OCR local.</summary>
    public bool UseCameraAnprEvents { get; set; } = false;

    /// <summary>
    /// Zonas de detección dibujadas sobre la imagen de la cámara. Si hay alguna, sólo
    /// se registran las detecciones cuyo centro cae dentro de una zona que admita ese
    /// tipo; el análisis se limita además al rectángulo que las envuelve.
    /// Sin zonas, se analiza según la región de interés clásica.
    /// </summary>
    public List<DetectionZone> Zones { get; set; } = new();

    /// <summary>Región de interés en porcentaje 0-100 (0,0,100,100 = fotograma completo).</summary>
    public double RoiXPercent { get; set; } = 0;
    public double RoiYPercent { get; set; } = 0;
    public double RoiWidthPercent { get; set; } = 100;
    public double RoiHeightPercent { get; set; } = 100;

    /// <summary>Ancho máximo al que se reescala el frame antes de inferir (0 = sin reescalado).</summary>
    public int MaxAnalysisWidth { get; set; } = 960;

    /// <summary>Segundos sin frames tras los cuales se reconecta.</summary>
    public int ReadTimeoutSeconds { get; set; } = 15;

    public string BuildRtspUrl(bool maskCredentials = false)
    {
        // Las cámaras USB no tienen URL: se devuelve una etiqueta informativa.
        if (IsUsb)
            return $"usb://{UsbDeviceIndex}";

        if (!string.IsNullOrWhiteSpace(RtspUrlOverride))
            return maskCredentials ? MaskUrlCredentials(RtspUrlOverride.Trim()) : RtspUrlOverride.Trim();

        var user = maskCredentials ? "***" : Uri.EscapeDataString(Username ?? "");
        var pass = maskCredentials ? "***" : Uri.EscapeDataString(Password ?? "");

        var sb = new StringBuilder("rtsp://");
        if (!string.IsNullOrEmpty(Username))
            sb.Append(user).Append(':').Append(pass).Append('@');
        sb.Append(Host).Append(':').Append(RtspPort);

        switch (Vendor)
        {
            case CameraVendor.Dahua:
                // subtype 0 = principal, 1 = secundario, 2 = tercero
                sb.Append("/cam/realmonitor?channel=").Append(Channel)
                  .Append("&subtype=").Append((int)Stream - 1);
                break;

            case CameraVendor.Hikvision:
            default:
                // Streaming/Channels/<canal*100 + perfil>:
                // 101 = canal 1 principal, 102 = canal 1 secundario, 201 = canal 2 principal…
                sb.Append("/Streaming/Channels/").Append(Math.Max(1, Channel) * 100 + (int)Stream);
                break;
        }

        return sb.ToString();
    }

    public string BuildIsapiBaseUrl()
    {
        var scheme = UseHttps ? "https" : "http";
        return $"{scheme}://{Host}:{HttpPort}";
    }

    private static string MaskUrlCredentials(string url)
    {
        var at = url.IndexOf('@');
        var schemeEnd = url.IndexOf("//", StringComparison.Ordinal);
        if (at < 0 || schemeEnd < 0 || at < schemeEnd) return url;
        return string.Concat(url.AsSpan(0, schemeEnd + 2), "***:***", url.AsSpan(at));
    }
}

/// <summary>
/// Área de interés dibujada sobre el fotograma, en porcentaje 0-100, con el tipo
/// de detección que se admite dentro de ella.
/// </summary>
public sealed class DetectionZone
{
    public string Name { get; set; } = "Zona";

    public double XPercent { get; set; }
    public double YPercent { get; set; }
    public double WidthPercent { get; set; } = 100;
    public double HeightPercent { get; set; } = 100;

    public bool Faces { get; set; } = true;
    public bool Plates { get; set; } = true;
    public bool Objects { get; set; } = true;
    public bool Texts { get; set; } = true;

    /// <summary>True si la zona admite el tipo de detección indicado.</summary>
    public bool Allows(IVZVision.Core.Detection.ObservationKind kind) => kind switch
    {
        IVZVision.Core.Detection.ObservationKind.Face => Faces,
        IVZVision.Core.Detection.ObservationKind.Plate => Plates,
        IVZVision.Core.Detection.ObservationKind.Object => Objects,
        _ => Texts,
    };

    /// <summary>True si el punto (en porcentaje del fotograma) cae dentro de la zona.</summary>
    public bool Contains(double xPercent, double yPercent) =>
        xPercent >= XPercent && xPercent <= XPercent + WidthPercent &&
        yPercent >= YPercent && yPercent <= YPercent + HeightPercent;
}
