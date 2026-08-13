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
            return new IsapiDeviceInfo(false, $"No se pudo contactar con la cámara: {ex.Message}");
        }
    }

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
