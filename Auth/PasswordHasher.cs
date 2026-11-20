using System.Security.Cryptography;
using System.Text;

// PBKDF2 with a per-password salt, so a stolen database does not hand out passwords.
// The format carries the iteration count, which lets a future change raise it without a migration.
public static class PasswordHasher {
    const int Iterations = 120_000, SaltSize = 16, KeySize = 32;
    const string Prefix = "pbkdf2$sha256";

    public static string Hash(string password) {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations,
            HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored) {
        var parts = stored.Split('$');
        if (parts.Length != 5 || parts[0] != "pbkdf2" || parts[1] != "sha256") return false;
        if (!int.TryParse(parts[2], out var iterations) || iterations < 1_000) return false;
        byte[] salt, expected;
        try {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        } catch (FormatException) {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
