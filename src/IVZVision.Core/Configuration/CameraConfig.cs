using System.Text;

namespace IVZVision.Core.Configuration;

public enum CameraSource
{
    /// <summary>Cámara IP por RTSP (Hikvision, Dahua, ONVIF…).</summary>
    Ip = 0,
    /// <summary>Cámara USB conectada al equipo donde corre la aplicación.</summary>
    Usb = 1,
}

public enum CameraVendor
{
    /// <summary>Hikvision y OEM compatibles (Hilook, Safire, LTS…).</summary>
    Hikvision = 0,
    Dahua = 1,
    /// <summary>Cualquier cámara ONVIF/RTSP: se usa la URL indicada a mano.</summary>
    Generic = 2,
}

public enum StreamProfile
{
    Main = 1,
    Sub = 2,
    Third = 3,
}

/// <summary>Parámetros de una cámara y de lo que se debe reconocer en ella.</summary>
public sealed class CameraConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Cámara 1";

    public bool Enabled { get; set; } = true;

    /// <summary>IP por RTSP o USB conectada al equipo.</summary>
    public CameraSource Source { get; set; } = CameraSource.Ip;

    // ---- Cámara IP -----------------------------------------------------
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

    public StreamProfile Stream { get; set; } = StreamProfile.Sub;

    /// <summary>Si tiene valor se usa como URL RTSP tal cual (obligatorio para <see cref="CameraVendor.Generic"/>).</summary>
    public string RtspUrlOverride { get; set; } = "";

    /// <summary>Fuerza transporte RTSP sobre TCP (recomendado: evita frames rotos).</summary>
    public bool UseTcpTransport { get; set; } = true;

    /// <summary>Escucha los eventos ANPR nativos de la cámara vía ISAPI, además del OCR local.</summary>
    public bool UseCameraAnprEvents { get; set; } = false;

    // ---- Cámara USB ----------------------------------------------------
    /// <summary>Índice del dispositivo (0 = primera cámara del sistema).</summary>
    public int DeviceIndex { get; set; } = 0;

    /// <summary>Ruta del dispositivo en Linux (<c>/dev/video0</c>). Si está vacía se usa el índice.</summary>
    public string DevicePath { get; set; } = "";

    /// <summary>Resolución solicitada al dispositivo (0 = la que traiga por defecto).</summary>
    public int CaptureWidth { get; set; } = 1280;
    public int CaptureHeight { get; set; } = 720;
    public double CaptureFps { get; set; } = 0;

    // ---- Qué se reconoce ------------------------------------------------
    public bool EnableFaceRecognition { get; set; } = true;

    public bool EnablePlateRecognition { get; set; } = true;

    /// <summary>Detecta personas, animales, vehículos y demás clases del detector de objetos.</summary>
    public bool EnableObjectDetection { get; set; } = true;

    /// <summary>Lee códigos QR y de barras presentes en la escena.</summary>
    public bool EnableCodeReading { get; set; } = false;

    /// <summary>Lee texto y escritura de la escena (carteles, etiquetas, documentos).</summary>
    public bool EnableTextReading { get; set; } = false;

    /// <summary>Aplica las reglas de actividad sospechosa sobre personas y animales.</summary>
    public bool EnableActivityAnalysis { get; set; } = true;

    // ---- Zonas ----------------------------------------------------------
    /// <summary>Región de interés en porcentaje 0-100 (0,0,100,100 = fotograma completo).</summary>
    public double RoiXPercent { get; set; } = 0;
    public double RoiYPercent { get; set; } = 0;
    public double RoiWidthPercent { get; set; } = 100;
    public double RoiHeightPercent { get; set; } = 100;

    /// <summary>Zona restringida para la regla de intrusión, en porcentaje del fotograma.</summary>
    public bool RestrictedZoneEnabled { get; set; } = false;
    public double RestrictedXPercent { get; set; } = 25;
    public double RestrictedYPercent { get; set; } = 25;
    public double RestrictedWidthPercent { get; set; } = 50;
    public double RestrictedHeightPercent { get; set; } = 50;

    // ---- Rendimiento -----------------------------------------------------
    /// <summary>Fotogramas por segundo que se envían al motor de reconocimiento (no afecta al vídeo mostrado).</summary>
    public double AnalysisFps { get; set; } = 6;

    /// <summary>Ancho máximo al que se reescala el frame antes de inferir (0 = sin reescalado).</summary>
    public int MaxAnalysisWidth { get; set; } = 1280;

    /// <summary>Segundos sin frames tras los cuales se reconecta.</summary>
    public int ReadTimeoutSeconds { get; set; } = 15;

    public bool IsUsb => Source == CameraSource.Usb;

    /// <summary>Texto corto que describe el origen, para listados y registros.</summary>
    public string DescribeSource(bool maskCredentials = true) => Source == CameraSource.Usb
        ? (string.IsNullOrWhiteSpace(DevicePath) ? $"USB · dispositivo {DeviceIndex}" : $"USB · {DevicePath}")
        : BuildRtspUrl(maskCredentials);

    public string BuildRtspUrl(bool maskCredentials = false)
    {
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
