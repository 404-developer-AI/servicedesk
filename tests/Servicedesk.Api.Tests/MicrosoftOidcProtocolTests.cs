using Servicedesk.Infrastructure.Auth.Microsoft;
using Xunit;

namespace Servicedesk.Api.Tests;

public class MicrosoftOidcProtocolTests
{
    // RFC 7636 Appendix B pins the expected code_challenge for a known
    // code_verifier. If this test ever fails we have silently broken the
    // PKCE handshake against every Azure AD tenant on earth.
    [Fact]
    public void ComputeCodeChallengeS256_matches_rfc7636_appendix_B_vector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        var actual = OidcProtocol.ComputeCodeChallengeS256(verifier);

        Assert.Equal(expectedChallenge, actual);
    }

    [Fact]
    public void ComputeCodeChallengeS256_is_deterministic()
    {
        var verifier = OidcProtocol.GenerateUrlSafeToken(48);

        var first = OidcProtocol.ComputeCodeChallengeS256(verifier);
        var second = OidcProtocol.ComputeCodeChallengeS256(verifier);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeCodeChallengeS256_different_verifiers_produce_different_challenges()
    {
        var a = OidcProtocol.ComputeCodeChallengeS256("verifier-one");
        var b = OidcProtocol.ComputeCodeChallengeS256("verifier-two");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeCodeChallengeS256_rejects_empty()
    {
        Assert.Throws<ArgumentException>(() => OidcProtocol.ComputeCodeChallengeS256(string.Empty));
    }

    [Fact]
    public void Base64UrlEncode_strips_padding_and_replaces_unsafe_chars()
    {
        // Input chosen to force both '+' and '/' in standard base64 and
        // padding. Standard encode → "/+8=", url-encode → "_-8".
        var bytes = new byte[] { 0xFF, 0xEF };
        var actual = OidcProtocol.Base64UrlEncode(bytes);

        Assert.Equal("_-8", actual);
        Assert.DoesNotContain("=", actual);
        Assert.DoesNotContain("+", actual);
        Assert.DoesNotContain("/", actual);
    }

    [Fact]
    public void GenerateUrlSafeToken_produces_url_safe_alphabet_only()
    {
        var token = OidcProtocol.GenerateUrlSafeToken(32);

        // base64url alphabet: A-Z a-z 0-9 - _
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
    }

    [Fact]
    public void GenerateUrlSafeToken_is_non_repeating_across_calls()
    {
        var a = OidcProtocol.GenerateUrlSafeToken(32);
        var b = OidcProtocol.GenerateUrlSafeToken(32);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GenerateUrlSafeToken_rejects_non_positive_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OidcProtocol.GenerateUrlSafeToken(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OidcProtocol.GenerateUrlSafeToken(-1));
    }
}
