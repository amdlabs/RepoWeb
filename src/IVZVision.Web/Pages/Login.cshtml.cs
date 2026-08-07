using System.Security.Claims;
using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

[AllowAnonymous]
// Sin validación antiforgery: el formulario sólo lleva las credenciales que teclea el
// usuario y así un token caducado (contenedor reiniciado, página antigua abierta)
// nunca bloquea el inicio de sesión con un error 400.
[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(IDbContextFactory<VisionDbContext> dbFactory, ILogger<LoginModel> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";

    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken ct)
    {
        try
        {
            // Se recortan espacios accidentales (copiar y pegar, autorrelleno).
            var username = (Username ?? "").Trim();
            var password = (Password ?? "").Trim();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var user = await db.SystemUsers
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Username == username, ct);

            // El motivo exacto va al registro (nunca la contraseña) para poder
            // diagnosticar sin revelar en pantalla qué parte falló.
            if (user is null)
            {
                _logger.LogWarning("Login fallido: el usuario «{User}» no existe (longitud de clave recibida: {Len})",
                                   username, password.Length);
                Error = "Usuario o contraseña incorrectos.";
                return Page();
            }

            if (!user.IsActive)
            {
                _logger.LogWarning("Login fallido: el usuario «{User}» está deshabilitado", username);
                Error = "Usuario o contraseña incorrectos.";
                return Page();
            }

            if (!SystemUser.VerifyPassword(password, user.PasswordHash))
            {
                _logger.LogWarning("Login fallido: contraseña incorrecta para «{User}» (longitud recibida: {Len})",
                                   username, password.Length);
                Error = "Usuario o contraseña incorrectos.";
                return Page();
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Role, user.Role.ToString()),
            };
            if (!string.IsNullOrEmpty(user.FullName))
                claims.Add(new Claim(ClaimTypes.GivenName, user.FullName));

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });

            _logger.LogInformation("Inicio de sesión de {User}", user.Username);

            return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
        }
        catch (Exception ex)
        {
            // Sin base de datos no se puede validar: se explica en pantalla en vez de dar un 500.
            _logger.LogError(ex, "No se pudo validar el inicio de sesión");
            Error = $"No se pudo consultar la base de datos: {ex.Message}";
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSalirAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
