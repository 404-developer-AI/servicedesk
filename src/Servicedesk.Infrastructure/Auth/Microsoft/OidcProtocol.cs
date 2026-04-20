using System.Security.Cryptography;
using System.Text;

namespace Servicedesk.Infrastructure.Auth.Microsoft;

/// Pure-function helpers for the OIDC challenge / callback flow. Extracted
/// so unit tests can exercise the crypto + encoding without a live Azure AD.
/// RFC references:
/// - <see href="https://datatracker.ietf.org/doc/html/rfc7636">RFC 7636</see> (PKCE)
/// - <see href="https://datatracker.ietf.org/doc/html/rfc4648#section-5">RFC 4648 §5</see> (base64url)
public static class OidcProtocol
{
    /// Computes the PKCE S256 code_challenge for a given code_verifier.
    /// Per RFC 7636 §4.2: code_challenge = BASE64URL(SHA256(ASCII(code_verifier))).
    public static string ComputeCodeChallengeS256(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    /// RFC 4648 §5 base64url (no padding). Used for PKCE challenge,
    /// state, nonce, and any other URL-safe token we mint.
    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// Cryptographically random URL-safe token. <paramref name="byteLength"/>
    /// controls entropy; 32 bytes → ~256 bits → 43 base64url characters.
    public static string GenerateUrlSafeToken(int byteLength)
    {
        if (byteLength <= 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteLength));
    }
}
