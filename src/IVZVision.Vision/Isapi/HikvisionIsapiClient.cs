using System.Net;
using System.Text;
using System.Xml.Linq;
using IVZVision.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace IVZVision.Vision.Isapi;

public sealed record IsapiDeviceInfo(bool Success, string Message, string? Model = null,
                                     string? SerialNumber = null, string? Firmware = null);

/// <summary>Matrícula leída por el propio ANPR de la cámara.</summary>
public sealed record CameraPlateEvent(string Plate, DateTimeOffset Timestamp, string? Country, string? Direction);

/// <summary>Canal de vídeo publicado por un DVR/NVR.</summary>
public sealed record IsapiChannel(int Channel, string Name, bool? Online);

public sealed record IsapiChannelList(bool Success, string Message, IReadOnlyList<IsapiChannel> Channels);

/// <summary>
/// Cliente del interfaz ISAPI de Hikvision. Sirve para dos cosas:
/// comprobar que las credenciales HTTP son correctas y, opcionalmente,
/// escuchar los eventos ANPR que genera la propia cámara.
/// </summary>
public sealed class HikvisionIsapiClient : IDisposable
{
    private readonly CameraConfig _camera;
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public HikvisionIsapiClient(CameraConfig camera, ILogger logger)
    {
        _camera = camera;
        _logger = logger;
        _http = CreateClient(camera);
    }

    private static HttpClient CreateClient(CameraConfig camera)
    {
        var baseUri = new Uri(camera.BuildIsapiBaseUrl());

        // Hikvision usa autenticación Digest por defecto (y Basic si se configura así).
        var credentials = new CredentialCache
        {
            { baseUri, "Digest", new NetworkCredential(camera.Username, camera.Password) },
            { baseUri, "Basic", new NetworkCredential(camera.Username, camera.Password) },
        };

        var handler = new HttpClientHandler
        {
            Credentials = credentials,
            PreAuthenticate = true,
            // Las cámaras traen certificados autofirmados: no hay CA que validar.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };

        return new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan, // el alertStream es una conexión larga
        };
    }

    /// <summary>Lee <c>/ISAPI/System/deviceInfo</c>: es la prueba de conexión del formulario.</summary>
    public async Task<IsapiDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _http.GetAsync("/ISAPI/System/deviceInfo", timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new IsapiDeviceInfo(false, "Usuario o contraseña incorrectos (HTTP 401).");

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new IsapiDeviceInfo(false,
                    $"El puerto {_camera.HttpPort} respondió HTTP pero no tiene el interfaz ISAPI (404): " +
                    "probablemente no es el puerto HTTP del DVR/cámara. Compruebe el puerto HTTP del equipo " +
                    "(por defecto 80) y, si accede desde fuera, que el router lo reenvíe al DVR.");

            if (!response.IsSuccessStatusCode)
                return new IsapiDeviceInfo(false, $"La cámara respondió {(int)response.StatusCode} {response.ReasonPhrase}.");

            var xml = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var doc = XDocument.Parse(xml);

            string? Value(string name) => doc.Root?.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

            var model = Value("model");
            return new IsapiDeviceInfo(true,
                $"Conexión correcta con {model ?? "el dispositivo"}.",
                model, Value("serialNumber"), Value("firmwareVersion"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IsapiDeviceInfo(false, "Tiempo de espera agotado al contactar con la cámara.");
        }
        catch (Exception ex)
        {
            return new IsapiDeviceInfo(false, $"No se pudo contactar con la cámara: {RootMessage(ex)}{PortHint()}");
        }
    }

    /// <summary>Mensaje de la excepción más interna: la causa real, no el envoltorio genérico.</summary>
    private static string RootMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null) current = current.InnerException;
        return current.Message;
    }

    private string PortHint() => _camera.HttpPort == 8000
        ? " Nota: el puerto 8000 suele ser el puerto SDK de Hikvision (iVMS/Hik-Connect), que no habla HTTP; " +
          "el interfaz ISAPI usa el puerto HTTP del equipo (por defecto 80)."
        : "";

    /// <summary>
    /// Lista los canales de vídeo que publica un DVR/NVR: los canales IP
    /// (<c>/ISAPI/ContentMgmt/InputProxy/channels</c>) y los analógicos
    /// (<c>/ISAPI/System/Video/inputs/channels</c>). El número de canal devuelto es
    /// el que se usa en la URL RTSP (canal×100 + perfil).
    /// </summary>
    public async Task<IsapiChannelList> ListChannelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            // Primero se valida la conexión ISAPI: así el error que llega al usuario es
            // el concreto (credenciales, puerto equivocado, equipo inalcanzable…).
            var device = await GetDeviceInfoAsync(timeout.Token).ConfigureAwait(false);
            if (!device.Success)
                return new IsapiChannelList(false, device.Message, Array.Empty<IsapiChannel>());

            var channels = new List<IsapiChannel>();

            // Canales IP (NVR y DVR híbridos).
            var proxyXml = await GetXmlOrNullAsync("/ISAPI/ContentMgmt/InputProxy/channels", timeout.Token)
                .ConfigureAwait(false);
            if (proxyXml is not null)
            {
                var online = await GetProxyOnlineMapAsync(timeout.Token).ConfigureAwait(false);

                foreach (var ch in proxyXml.Descendants()
                             .Where(e => e.Name.LocalName.Equals("InputProxyChannel", StringComparison.OrdinalIgnoreCase)))
                {
                    var id = ChildValue(ch, "id");
                    if (!int.TryParse(id, out var number)) continue;

                    var name = ChildValue(ch, "name");
                    channels.Add(new IsapiChannel(number,
                        string.IsNullOrWhiteSpace(name) ? $"Canal {number}" : name.Trim(),
                        online.TryGetValue(number, out var isOn) ? isOn : null));
                }
            }

            // Canales analógicos (DVR clásicos) o el propio canal de una cámara standalone.
            var inputsXml = await GetXmlOrNullAsync("/ISAPI/System/Video/inputs/channels", timeout.Token)
                .ConfigureAwait(false);
            if (inputsXml is not null)
            {
                foreach (var ch in inputsXml.Descendants()
                             .Where(e => e.Name.LocalName.Equals("VideoInputChannel", StringComparison.OrdinalIgnoreCase)))
                {
                    var id = ChildValue(ch, "id");
                    if (!int.TryParse(id, out var number)) continue;
                    if (channels.Any(c => c.Channel == number)) continue;

                    var enabled = ChildValue(ch, "videoInputEnabled");
                    var name = ChildValue(ch, "name");
                    channels.Add(new IsapiChannel(number,
                        string.IsNullOrWhiteSpace(name) ? $"Canal {number}" : name.Trim(),
                        bool.TryParse(enabled, out var on) ? on : null));
                }
            }

            if (channels.Count == 0)
                return new IsapiChannelList(false,
                    "El dispositivo no publicó ningún canal por ISAPI. Compruebe credenciales y que sea un DVR/NVR Hikvision o compatible.",
                    Array.Empty<IsapiChannel>());

            var ordered = channels.OrderBy(c => c.Channel).ToList();
            return new IsapiChannelList(true, $"Se han encontrado {ordered.Count} canal(es).", ordered);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IsapiChannelList(false, "Tiempo de espera agotado al consultar los canales.", Array.Empty<IsapiChannel>());
        }
        catch (Exception ex)
        {
            return new IsapiChannelList(false, $"No se pudieron consultar los canales: {ex.Message}", Array.Empty<IsapiChannel>());
        }
    }

    /// <summary>Estado en línea de cada canal IP, si el equipo lo publica.</summary>
    private async Task<Dictionary<int, bool>> GetProxyOnlineMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<int, bool>();

        var xml = await GetXmlOrNullAsync("/ISAPI/ContentMgmt/InputProxy/channels/status", ct).ConfigureAwait(false);
        if (xml is null) return map;

        foreach (var st in xml.Descendants()
                     .Where(e => e.Name.LocalName.Equals("InputProxyChannelStatus", StringComparison.OrdinalIgnoreCase)))
        {
            if (int.TryParse(ChildValue(st, "id"), out var number)
                && bool.TryParse(ChildValue(st, "online"), out var online))
                map[number] = online;
        }

        return map;
    }

    private async Task<XDocument?> GetXmlOrNullAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return XDocument.Parse(xml);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ISAPI {Path} no disponible", path);
            return null;
        }
    }

    private static string? ChildValue(XElement parent, string name) => parent.Elements()
        .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>
    /// Se suscribe a <c>/ISAPI/Event/notification/alertStream</c> y va devolviendo
    /// las matrículas que reconoce la cámara. La conexión permanece abierta.
    /// </summary>
    public async IAsyncEnumerable<CameraPlateEvent> StreamPlateEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ISAPI/Event/notification/alertStream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                        .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var buffer = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break; // la cámara cerró el flujo

            // El flujo es multipart: cada parte va separada por una línea de frontera "--boundary".
            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                var chunk = buffer.ToString();
                buffer.Clear();

                var evt = TryParseAnpr(chunk);
                if (evt is not null) yield return evt;
                continue;
            }

            // Se descartan las cabeceras de cada parte (Content-Type, Content-Length…).
            if (buffer.Length == 0 && !line.TrimStart().StartsWith("<", StringComparison.Ordinal))
                continue;

            buffer.AppendLine(line);
        }

        var tail = TryParseAnpr(buffer.ToString());
        if (tail is not null) yield return tail;
    }

    private CameraPlateEvent? TryParseAnpr(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains("<EventNotificationAlert", StringComparison.Ordinal))
            return null;

        try
        {
            var doc = XDocument.Parse(xml.Trim());

            string? Find(string name) => doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

            var eventType = Find("eventType");
            if (eventType is null || !eventType.Contains("ANPR", StringComparison.OrdinalIgnoreCase))
                return null;

            var plate = Find("licensePlate");
            if (string.IsNullOrWhiteSpace(plate)) return null;

            var timestamp = DateTimeOffset.TryParse(Find("dateTime"), out var parsed) ? parsed : DateTimeOffset.Now;

            return new CameraPlateEvent(plate.Trim(), timestamp, Find("country"), Find("direction"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Parte del alertStream ISAPI no se pudo interpretar");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
