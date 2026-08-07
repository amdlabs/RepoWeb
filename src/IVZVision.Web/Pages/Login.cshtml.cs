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
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var user = await db.SystemUsers
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Username == Username.Trim(), ct);

            if (user is null || !user.IsActive || !SystemUser.VerifyPassword(Password, user.PasswordHash))
            {
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
