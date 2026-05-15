using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Servicedesk.Infrastructure.Surveys;

/// Mints + validates the single-use tokens embedded in survey invitation
/// links. Mirrors <c>IIntakeFormTokenService</c> token-for-token so the
/// public-URL semantics are identical between intake forms and surveys.
///
/// <para>The raw token is 32 bytes of CSPRNG, base64url-encoded for URL use.
/// It never touches the DB in plaintext. We persist two transformations:</para>
/// <list type="bullet">
/// <item><c>token_hash</c> = sha256(raw) — lookup key (DB unique index).</item>
/// <item><c>token_cipher</c> = DataProtection-encrypted raw bytes — lets a
/// future "copy survey link" admin action recover the URL without resending
/// the invitation.</item>
/// </list>
public interface ISurveyTokenService
{
    (string Raw, byte[] Hash, byte[] Cipher) Mint();
    byte[]? HashForLookup(string rawFromUrl);
}

public sealed class SurveyTokenService : ISurveyTokenService
{
    /// Rotating this string invalidates every survey token-cipher minted
    /// under the old value. Kept stable across deploys; bump only as part
    /// of a deliberate security migration.
    public const string DataProtectionPurpose = "Servicedesk.Surveys.Token.v1";

    private readonly IDataProtector _protector;

    public SurveyTokenService(IDataProtectionProvider dataProtection)
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
        if (string.IsNullOrWhiteSpace(rawFromUrl) || rawFromUrl.Length > 64) return null;

        var bytes = TryBase64UrlDecode(rawFromUrl);
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
