using System.Security.Cryptography;
using System.Text;

namespace IVZVision.Core.Configuration;

/// <summary>
/// Cifra las contraseñas antes de escribirlas en el JSON de configuración usando
/// DPAPI con ámbito de máquina: cualquier proceso del mismo equipo puede descifrarlas,
/// pero el fichero deja de ser legible si se copia a otro PC o se abre a mano.
/// En sistemas no Windows se guarda el valor tal cual (DPAPI no está disponible).
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "enc:v1:";

    // Entropía fija: liga el cifrado a esta aplicación sin exigir gestión de claves.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IVZVision.Secrets.v1");

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain ?? "";
        if (!OperatingSystem.IsWindows()) return plain;

        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (CryptographicException)
        {
            // Mejor guardar en claro que perder la contraseña.
            return plain;
        }
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!IsProtected(stored)) return stored; // valor heredado en claro
        if (!OperatingSystem.IsWindows()) return "";

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            // Fichero copiado de otra máquina o base64 corrupto: se pide de nuevo la clave.
            return "";
        }
    }

    /// <summary>Cifra en sitio todas las contraseñas de la configuración (para persistir).</summary>
    public static void ProtectSecrets(AppConfig config)
    {
        config.Database.Password = Protect(config.Database.Password);
        foreach (var camera in config.Cameras)
            camera.Password = Protect(camera.Password);
    }

    /// <summary>Descifra en sitio todas las contraseñas (tras leer el fichero).</summary>
    public static void UnprotectSecrets(AppConfig config)
    {
        config.Database.Password = Unprotect(config.Database.Password);
        foreach (var camera in config.Cameras)
            camera.Password = Unprotect(camera.Password);
    }
}
