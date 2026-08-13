using Microsoft.AspNetCore.SignalR;

namespace IVZVision.Web.Hubs;

/// <summary>Canal en tiempo real con el navegador: reconocimientos y estado de cada cámara.</summary>
public class DetectionHub : Hub
{
    public const string AllCamerasGroup = "todas";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, AllCamerasGroup);
        await base.OnConnectedAsync();
    }

    /// <summary>Filtra las notificaciones a una cámara concreta.</summary>
    public async Task Suscribir(string cameraId)
    {
        if (Guid.TryParse(cameraId, out var id))
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(id));
    }

    public async Task Desuscribir(string cameraId)
    {
        if (Guid.TryParse(cameraId, out var id))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(id));
    }

    public static string GroupFor(Guid cameraId) => $"camara-{cameraId:N}";
}
