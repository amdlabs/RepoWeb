using System.Security.Claims;
using IVZVision.Data.Entities;

namespace IVZVision.Web.Services;

/// <summary>
/// Comprobaciones de rol para los manejadores POST de páginas que son visibles
/// para todos los usuarios pero cuyas acciones de escritura exigen operador.
/// </summary>
public static class RoleGuard
{
    public static bool CanEdit(ClaimsPrincipal user) =>
        user.IsInRole(nameof(SystemUserRole.Administrator)) ||
        user.IsInRole(nameof(SystemUserRole.Operator));
}
