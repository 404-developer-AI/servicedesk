using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Auth;

/// Argon2id password hasher. Parameters (memory, iterations, parallelism) are
/// read from the settings store, so admins can tune them without a redeploy.
/// The encoded format is a PHC-style string
/// <c>$argon2id$v=19$m={kb},t={iters},p={lanes}${saltB64}${hashB64}</c>
/// — self-describing, so <see cref="Verify"/> can detect a parameter drift
/// and signal a transparent rehash on the next successful login.
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly ISettingsService _settings;

    public Argon2idPasswordHasher(ISettingsService settings)
    {
        _settings = settings;
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var (memoryKb, iterations, parallelism) = LoadParameters();
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = ComputeHash(password, salt, memoryKb, iterations, parallelism);

        return Encode(memoryKb, iterations, parallelism, salt, hash);
    }

    public bool Verify(string encoded, string password, out bool rehashNeeded)
    {
        rehashNeeded = false;
        ArgumentNullException.ThrowIfNull(password);

        if (!TryDecode(encoded, out var parts))
        {
            return false;
        }

        var computed = ComputeHash(password, parts.Salt, parts.MemoryKb, parts.Iterations, parts.Parallelism);
        if (!CryptographicOperations.FixedTimeEquals(computed, parts.Hash))
        {
            return false;
        }

        var (memoryKb, iterations, parallelism) = LoadParameters();
        if (parts.MemoryKb != memoryKb || parts.Iterations != iterations || parts.Parallelism != parallelism)
        {
            rehashNeeded = true;
        }
        return true;
    }

    private (int MemoryKb, int Iterations, int Parallelism) LoadParameters()
    {
        // Blocking waits on a cache hit after first prime — acceptable for a
        // hasher that's already deliberately expensive.
        var memoryKb = _settings.GetAsync<int>(SettingKeys.Security.PasswordArgon2MemoryKb).GetAwaiter().GetResult();
        var iterations = _settings.GetAsync<int>(SettingKeys.Security.PasswordArgon2Iterations).GetAwaiter().GetResult();
        var parallelism = _settings.GetAsync<int>(SettingKeys.Security.PasswordArgon2Parallelism).GetAwaiter().GetResult();
        return (memoryKb, iterations, parallelism);
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(HashBytes);
    }

    private static string Encode(int memoryKb, int iterations, int parallelism, byte[] salt, byte[] hash) =>
        string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={memoryKb},t={iterations},p={parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");

    private readonly record struct DecodedHash(int MemoryKb, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);

    private static bool TryDecode(string encoded, out DecodedHash parts)
    {
        parts = default;
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var segments = encoded.Split('$');
        if (segments.Length != 6 || segments[0].Length != 0 || segments[1] != "argon2id" || segments[2] != "v=19")
        {
            return false;
        }

        var paramPairs = segments[3].Split(',');
        int memoryKb = 0, iterations = 0, parallelism = 0;
        foreach (var pair in paramPairs)
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
            {
                return false;
            }
            switch (kv[0])
            {
                case "m": memoryKb = num; break;
                case "t": iterations = num; break;
                case "p": parallelism = num; break;
            }
        }
        if (memoryKb <= 0 || iterations <= 0 || parallelism <= 0)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(segments[4]);
            var hash = Convert.FromBase64String(segments[5]);
            parts = new DecodedHash(memoryKb, iterations, parallelism, salt, hash);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
