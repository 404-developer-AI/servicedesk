using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Servicedesk.Infrastructure.Security;

/// The one implementation of the public-link tokens (intake forms, surveys,
/// campaign answers). The raw token is 32 bytes of CSPRNG, base64url-encoded
/// for the URL (≈43 chars); it never touches the database in plaintext.
/// Persisted instead: <c>sha256(raw)</c> as the lookup key (unique index —
/// a one-way function of a 256-bit secret, so byte equality leaks nothing)
/// and the DataProtection-encrypted raw bytes, so a later mail can embed the
/// same link again without minting a new one. Each feature uses its own
/// DataProtection purpose; rotating a purpose invalidates its ciphertexts.
public sealed class LinkTokenCodec
{
    private readonly IDataProtector _protector;

    public LinkTokenCodec(IDataProtectionProvider dataProtection, string purpose)
    {
        _protector = dataProtection.CreateProtector(purpose);
    }

    public (string Raw, byte[] Hash, byte[] Cipher) Mint()
    {
        Span<byte> rawBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(rawBytes);
        return (Base64UrlEncode(rawBytes), SHA256.HashData(rawBytes), _protector.Protect(rawBytes.ToArray()));
    }

    /// Recovers the raw token from a stored ciphertext; null when it can no
    /// longer be decrypted (purpose rotated, key ring lost).
    public string? Reveal(byte[] cipher)
    {
        try { return Base64UrlEncode(_protector.Unprotect(cipher)); }
        catch (CryptographicException) { return null; }
    }

    /// The lookup hash for a token from a URL, or null when the input is not
    /// a well-formed base64url 32-byte value — garbage is rejected before any
    /// database round-trip, which keeps enumeration scans cheap.
    public static byte[]? HashForLookup(string? rawFromUrl)
    {
        if (string.IsNullOrWhiteSpace(rawFromUrl) || rawFromUrl.Length > 64) return null;
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
        // Strict: only [A-Za-z0-9_-] is accepted.
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
