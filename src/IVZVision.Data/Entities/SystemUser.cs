using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;

namespace IVZVision.Data.Entities;

public enum SystemUserRole
{
    Administrator = 0,
    Operator = 1,
    Viewer = 2,
}

/// <summary>Usuario de la propia aplicación (acceso a la web y a la API).</summary>
public class SystemUser
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Username { get; set; } = "";

    [MaxLength(200)]
    public string FullName { get; set; } = "";

    /// <summary>Hash PBKDF2 con el formato «iteraciones.salBase64.hashBase64».</summary>
    [MaxLength(500)]
    public string PasswordHash { get; set; } = "";

    public SystemUserRole Role { get; set; } = SystemUserRole.Viewer;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ---- Hash de contraseñas (PBKDF2-SHA256) -----------------------------

    private const int Iterations = 100_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        try
        {
            var parts = stored.Split('.');
            if (parts.Length != 3) return false;

            var iterations = int.Parse(parts[0]);
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
