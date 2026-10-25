using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Vellum.Domain;

namespace Vellum.Engine.Security;

/// <summary>SEC-202: salted, memory-hard Argon2id hashes. Format: <c>argon2id$m=..,t=..,p=..$salt$hash</c>.</summary>
public static class PasswordHasher
{
    private const int MemoryKb = 19456; // OWASP minimum profile: 19 MiB, t=2, p=1
    private const int Iterations = 2;
    private const int Parallelism = 1;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(password, salt, MemoryKb, Iterations, Parallelism);
        return $"argon2id$m={MemoryKb},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return false;
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "argon2id") return false;
        var p = parts[1].Split(',').Select(x => x.Split('=')).ToDictionary(x => x[0], x => int.Parse(x[1]));
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Compute(password, salt, p["m"], p["t"], p["p"]);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(32);
    }
}

/// <summary>SEC-201/204: always-on password rules that administrators may only make stricter.</summary>
public static class PasswordPolicy
{
    // A small built-in breached/common password list (SEC-201 Vellum extension). Production would use a k-anonymity service.
    private static readonly HashSet<string> Breached = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "Password123!", "Passw0rd!", "Welcome123!", "Qwerty123!", "Admin123!",
        "Letmein123!", "123456789012", "P@ssw0rd1234", "Summer2026!", "Winter2026!", "Changeme123!",
    };

    public static IEnumerable<string> Describe(SystemSettings s) =>
    [
        $"At least {s.PasswordMinLength} characters",
        "At least one uppercase letter, one digit and one special character",
        $"Different from your last {s.PasswordHistoryCount} passwords",
        "Not a commonly used or breached password",
    ];

    public static List<string> Validate(string password, SystemSettings s, User? user, IEnumerable<string> recentHashes)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password)) { errors.Add("Enter a password."); return errors; }
        if (password.Length < Math.Max(8, s.PasswordMinLength)) errors.Add($"Use at least {Math.Max(8, s.PasswordMinLength)} characters.");
        if (!password.Any(char.IsUpper)) errors.Add("Add at least one uppercase letter.");
        if (!password.Any(char.IsDigit)) errors.Add("Add at least one digit.");
        if (password.All(char.IsLetterOrDigit)) errors.Add("Add at least one special character.");
        if (Breached.Contains(password)) errors.Add("This password appears in a list of breached passwords. Choose another.");
        if (user != null && password.Contains(user.Username, StringComparison.OrdinalIgnoreCase)) errors.Add("Don't include your username.");
        if (recentHashes.Any(h => PasswordHasher.Verify(password, h))) errors.Add($"You used this password recently. Choose one you haven't used in your last {s.PasswordHistoryCount} passwords.");
        return errors;
    }
}
