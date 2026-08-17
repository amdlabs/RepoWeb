using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IVZVision.Web.Pages;

/// <summary>
/// Muro de monitoreo: todas las cámaras configuradas en una cuadrícula de 4, 6,
/// 8 o 12 posiciones con el vídeo ya procesado (cuadrantes de rostros, matrículas
/// y objetos). Los datos los sirve la API (/api/camaras) y la lógica vive en
/// wwwroot/js/monitoreo.js.
/// </summary>
public class MonitoreoModel : PageModel
{
    public void OnGet() { }
}
