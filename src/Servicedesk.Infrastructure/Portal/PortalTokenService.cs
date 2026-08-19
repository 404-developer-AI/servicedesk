using System.Security.Cryptography;

namespace Servicedesk.Infrastructure.Portal;

/// Mints + hashes the one-time secrets embedded in portal mail links
/// (email verification, invitation, password reset). Token-for-token the
/// survey/intake model: 32 bytes CSPRNG, base64url in the URL, only the
/// SHA-256 is persisted (unique lookup key). No cipher copy is kept — a
/// lost link is re-issued by minting a fresh token, never recovered.
public interface IPortalTokenService
{
    (string Raw, byte[] Hash) Mint();
    byte[]? HashForLookup(string rawFromUrl);
}

public sealed class PortalTokenService : IPortalTokenService
{
    public (string Raw, byte[] Hash) Mint()
    {
        Span<byte> rawBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(rawBytes);
        return (Base64UrlEncode(rawBytes), SHA256.HashData(rawBytes));
    }

    public byte[]? HashForLookup(string rawFromUrl)
    {
        if (string.IsNullOrWhiteSpace(rawFromUrl) || rawFromUrl.Length > 64) return null;
        var bytes = TryBase64UrlDecode(rawFromUrl.Trim());
        if (bytes is null || bytes.Length != 32) return null;
        return SHA256.HashData(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[]? TryBase64UrlDecode(string input)
    {
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
