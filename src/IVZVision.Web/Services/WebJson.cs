using System.Text.Json;

namespace IVZVision.Web.Services;

public static class WebJson
{
    /// <summary>
    /// Mismas reglas que usa SignalR por defecto, para que el JSON incrustado en la
    /// página y el que llega por el hub tengan exactamente los mismos nombres.
    /// </summary>
    public static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Camel);
}
