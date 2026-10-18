using System.Security.Cryptography;

namespace OwlSamba.Core;

/// <summary>PBKDF2-SHA256 password hashes. Also verifies bcrypt hashes migrated from OwlSamba v1.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(password)) return false;
        try
        {
            if (stored.StartsWith("$2", StringComparison.Ordinal))
                return BCrypt.Net.BCrypt.Verify(password, stored);

            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
            var iterations = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public static bool NeedsRehash(string stored) => !stored.StartsWith("pbkdf2$", StringComparison.Ordinal);
}
