using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Servicedesk.Infrastructure.IntakeForms;

/// Mints + validates the single-use tokens embedded in intake-form links.
///
/// <para>The raw token is 32 bytes of CSPRNG, base64url-encoded for URL use.
/// It never touches the DB in plaintext. We persist two transformations:</para>
/// <list type="bullet">
/// <item><c>token_hash</c> = sha256(raw) — lookup key (DB unique index).</item>
/// <item><c>token_cipher</c> = DataProtection-encrypted raw bytes — lets a
/// future feature re-surface the link (e.g. "copy form URL") without the
/// agent having to resend.</item>
/// </list>
/// <para>Compare by <see cref="byte"/>-array equality on the hash column —
/// Postgres already uses the unique index for O(1) lookup, and the hash
/// gives no cover for timing-oracle attacks because it's a one-way
/// function of a 256-bit secret.</para>
public interface IIntakeFormTokenService
{
    /// Generates a fresh token. Returns the raw string (base64url, ≈43 chars)
    /// that goes into the mail link, the sha256 hash for DB lookup, and the
    /// DataProtection-encrypted ciphertext for optional redisplay.
    (string Raw, byte[] Hash, byte[] Cipher) Mint();

    /// Hashes a caller-supplied raw token for lookup. Returns null if the
    /// input is not a well-formed base64url-encoded 32-byte blob — rejecting
    /// garbage early avoids a DB round-trip on obvious scans.
    byte[]? HashForLookup(string rawFromUrl);
}

public sealed class IntakeFormTokenService : IIntakeFormTokenService
{
    /// Scoped DataProtection purpose — rotating this string invalidates every
    /// ciphertext minted under the old value. Kept stable across deploys.
    public const string DataProtectionPurpose = "Servicedesk.IntakeForms.Token.v1";

    private readonly IDataProtector _protector;

    public IntakeFormTokenService(IDataProtectionProvider dataProtection)
    {
        _protector = dataProtection.CreateProtector(DataProtectionPurpose);
    }

    public (string Raw, byte[] Hash, byte[] Cipher) Mint()
    {
        Span<byte> rawBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(rawBytes);

        var raw = Base64UrlEncode(rawBytes);
        var hash = SHA256.HashData(rawBytes);
        var cipher = _protector.Protect(rawBytes.ToArray());

        return (raw, hash, cipher);
    }

    public byte[]? HashForLookup(string rawFromUrl)
    {
        if (string.IsNullOrWhiteSpace(rawFromUrl) || rawFromUrl.Length > 64)
        {
            // 43 is the canonical length of base64url-encoded 32 bytes;
            // anything clearly out of range is rejected before we touch
            // the DB to keep token-enumeration scans cheap.
            return null;
        }

        var bytes = TryBase64UrlDecode(rawFromUrl);
        if (bytes is null || bytes.Length != 32) return null;

        return SHA256.HashData(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        // URL-safe variant: strip padding, swap +/-, swap /_.
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[]? TryBase64UrlDecode(string input)
    {
        // Reverse the three URL-safe substitutions and pad back to a
        // multiple of 4. We're strict: only [A-Za-z0-9_-] is accepted.
        foreach (var c in input)
        {
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) return null;
        }

        var padLen = (4 - (input.Length % 4)) % 4;
        var normalized = input.Replace('-', '+').Replace('_', '/') + new string('=', padLen);

        try { return Convert.FromBase64String(normalized); }
        catch (FormatException) { return null; }
    }
}
