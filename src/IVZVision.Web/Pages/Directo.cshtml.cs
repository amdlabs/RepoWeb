using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IVZVision.Web.Pages;

/// <summary>Directo y Monitoreo se fusionaron: la URL antigua redirige al muro.</summary>
public class DirectoModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Monitoreo");
}
