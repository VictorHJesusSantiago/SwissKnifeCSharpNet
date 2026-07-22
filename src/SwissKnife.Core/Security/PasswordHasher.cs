using System.Security.Cryptography;

namespace SwissKnife.Core.Security;

/// <summary>
/// API-012: hash de senha forte (PBKDF2-HMACSHA256, 100k iterações, salt aleatório de 16
/// bytes). Formato persistido: "{iterations}.{saltBase64}.{hashBase64}", auto-descritivo
/// para permitir aumentar iterações no futuro sem invalidar hashes antigos.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool IsStrong(string password, out string? reason)
    {
        reason = password.Length < 14 ? "A senha deve ter ao menos 14 caracteres." : null;
        return reason is null;
    }
}
