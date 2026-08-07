using IVZVision.Data;
using IVZVision.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IVZVision.Web.Pages;

/// <summary>Gestión de los usuarios de la aplicación (tabla SystemUsers).</summary>
public class UsuariosModel : PageModel
{
    private readonly IDbContextFactory<VisionDbContext> _dbFactory;

    public UsuariosModel(IDbContextFactory<VisionDbContext> dbFactory) => _dbFactory = dbFactory;

    public IReadOnlyList<SystemUser> Users { get; private set; } = Array.Empty<SystemUser>();
    public string? DatabaseError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            Users = await db.SystemUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }

    public async Task<IActionResult> OnPostCrearAsync(string username, string fullName, string password,
                                                      SystemUserRole role, CancellationToken ct)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0 || string.IsNullOrEmpty(password))
        {
            TempData["Error"] = "Hay que indicar usuario y contraseña.";
            return RedirectToPage();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (await db.SystemUsers.AnyAsync(u => u.Username == username, ct))
        {
            TempData["Error"] = $"El usuario «{username}» ya existe.";
            return RedirectToPage();
        }

        db.SystemUsers.Add(new SystemUser
        {
            Username = username,
            FullName = (fullName ?? "").Trim(),
            PasswordHash = SystemUser.HashPassword(password),
            Role = role,
        });
        await db.SaveChangesAsync(ct);

        TempData["Ok"] = $"Usuario «{username}» creado.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClaveAsync(int id, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
        {
            TempData["Error"] = "La nueva contraseña no puede estar vacía.";
            return RedirectToPage();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var user = await db.SystemUsers.FindAsync(new object[] { id }, ct);
        if (user is not null)
        {
            user.PasswordHash = SystemUser.HashPassword(password);
            await db.SaveChangesAsync(ct);
            TempData["Ok"] = $"Contraseña de «{user.Username}» actualizada.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAlternarAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var user = await db.SystemUsers.FindAsync(new object[] { id }, ct);
        if (user is not null)
        {
            user.IsActive = !user.IsActive;
            await db.SaveChangesAsync(ct);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEliminarAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (await db.SystemUsers.CountAsync(ct) <= 1)
        {
            TempData["Error"] = "No se puede eliminar el último usuario del sistema.";
            return RedirectToPage();
        }

        var user = await db.SystemUsers.FindAsync(new object[] { id }, ct);
        if (user is not null)
        {
            db.SystemUsers.Remove(user);
            await db.SaveChangesAsync(ct);
            TempData["Ok"] = $"Usuario «{user.Username}» eliminado.";
        }

        return RedirectToPage();
    }
}
