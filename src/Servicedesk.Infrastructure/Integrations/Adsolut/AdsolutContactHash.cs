using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// SHA-256 over the canonicalized field-set the v0.0.28 Adsolut contacts
/// pull mirrors per <c>contact_companies</c> link. Same length-prefixed
/// canonicalisation as <see cref="AdsolutCompanyHash"/>, so the contract
/// (4-byte big-endian UTF-8 length, then bytes) is consistent across the
/// integration.
///
/// Hashed fields (locked-in for v0.0.28, do not change without bumping
/// every stored hash on contact_companies.adsolut_synced_hash):
/// <list type="bullet">
/// <item><c>FirstName</c></item>
/// <item><c>LastName</c></item>
/// <item><c>Phone</c></item>
/// <item><c>MobilePhone</c></item>
/// </list>
///
/// <c>Email</c> is intentionally NOT in the hash — it is the match-key,
/// never overwritten after the initial match. <c>Active</c> is tracked
/// per-link as a real column (<c>contact_companies.adsolut_active</c>),
/// not via the hash, because Adsolut does not bump <c>lastModified</c> on
/// active-flips so the slow reconcile-loop must compare it directly
/// regardless of hash equality.
public static class AdsolutContactHash
{
    public static byte[] Compute(AdsolutContactHashInput input)
    {
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();

        // Stable order is part of the hash contract — never reorder, never
        // insert without bumping every stored hash.
        Append(ms, Canonicalize(input.FirstName));
        Append(ms, Canonicalize(input.LastName));
        Append(ms, Canonicalize(input.Phone));
        Append(ms, Canonicalize(input.MobilePhone));

        ms.Position = 0;
        return sha.ComputeHash(ms);
    }

    private static string Canonicalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.Trim().Normalize(NormalizationForm.FormC);
    }

    private static void Append(MemoryStream ms, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> lengthHeader = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthHeader, bytes.Length);
        ms.Write(lengthHeader);
        ms.Write(bytes);
    }
}

/// Inputs for <see cref="AdsolutContactHash.Compute"/>. The same shape is
/// projected from both the inbound Adsolut contact (during pull) and the
/// SD-side contact row + link (during the v0.0.29 push), so a successful
/// round-trip produces identical hashes on both sides.
public sealed record AdsolutContactHashInput(
    string? FirstName,
    string? LastName,
    string? Phone,
    string? MobilePhone);
