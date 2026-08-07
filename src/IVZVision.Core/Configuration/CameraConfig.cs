using System.Text;

namespace IVZVision.Core.Configuration;

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

    public StreamProfile Stream { get; set; } = StreamProfile.Sub;

    /// <summary>Si tiene valor se usa como URL RTSP tal cual (obligatorio para <see cref="CameraVendor.Generic"/>).</summary>
    public string RtspUrlOverride { get; set; } = "";

    /// <summary>Fuerza transporte RTSP sobre TCP (recomendado: evita frames rotos).</summary>
    public bool UseTcpTransport { get; set; } = true;

    /// <summary>Fotogramas por segundo que se envían al motor de reconocimiento (no afecta al vídeo mostrado).</summary>
    public double AnalysisFps { get; set; } = 6;

    public bool EnableFaceRecognition { get; set; } = true;

    public bool EnablePlateRecognition { get; set; } = true;

    /// <summary>Escucha los eventos ANPR nativos de la cámara vía ISAPI, además del OCR local.</summary>
    public bool UseCameraAnprEvents { get; set; } = false;

    /// <summary>Región de interés en porcentaje 0-100 (0,0,100,100 = fotograma completo).</summary>
    public double RoiXPercent { get; set; } = 0;
    public double RoiYPercent { get; set; } = 0;
    public double RoiWidthPercent { get; set; } = 100;
    public double RoiHeightPercent { get; set; } = 100;

    /// <summary>Ancho máximo al que se reescala el frame antes de inferir (0 = sin reescalado).</summary>
    public int MaxAnalysisWidth { get; set; } = 1280;

    /// <summary>Segundos sin frames tras los cuales se reconecta.</summary>
    public int ReadTimeoutSeconds { get; set; } = 15;

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
