using Microsoft.AspNetCore.DataProtection;
using Servicedesk.Infrastructure.Security;

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
    /// Scoped DataProtection purpose — rotating this string invalidates every
    /// ciphertext minted under the old value. Kept stable across deploys.
    public const string DataProtectionPurpose = "Servicedesk.Surveys.Token.v1";

    private readonly LinkTokenCodec _codec;

    public SurveyTokenService(IDataProtectionProvider dataProtection)
    {
        _codec = new LinkTokenCodec(dataProtection, DataProtectionPurpose);
    }

    public (string Raw, byte[] Hash, byte[] Cipher) Mint() => _codec.Mint();

    public byte[]? HashForLookup(string rawFromUrl) => LinkTokenCodec.HashForLookup(rawFromUrl);
}
