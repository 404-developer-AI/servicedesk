using Microsoft.AspNetCore.DataProtection;
using Servicedesk.Infrastructure.Security;

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

    private readonly LinkTokenCodec _codec;

    public IntakeFormTokenService(IDataProtectionProvider dataProtection)
    {
        _codec = new LinkTokenCodec(dataProtection, DataProtectionPurpose);
    }

    public (string Raw, byte[] Hash, byte[] Cipher) Mint() => _codec.Mint();

    public byte[]? HashForLookup(string rawFromUrl) => LinkTokenCodec.HashForLookup(rawFromUrl);
}
