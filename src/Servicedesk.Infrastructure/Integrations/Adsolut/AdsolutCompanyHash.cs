using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// SHA-256 over the canonicalized field-set the v0.0.27 Adsolut push tak
/// mirrors between the local <c>companies</c> row and the Adsolut Customer.
/// Both sides of the sync (the inbound upserter and the outbound push) call
/// this function with the same SD-side shape, so a successful round-trip
/// produces the exact same hash on both sides — that equality is what makes
/// the echo-pull a no-op (no UPDATE, no SignalR broadcast, no audit-ruis)
/// and what stops the push-tak from issuing a redundant PUT.
///
/// Canonicalization (locked-in for v0.0.27, do not change without bumping
/// every stored hash):
/// <list type="bullet">
/// <item>Each field is trimmed; null is treated as empty.</item>
/// <item>Email is lowercased (Adsolut + SD both treat email
/// case-insensitively, but agents enter mixed case).</item>
/// <item>Each string is Unicode-NFC-normalized so accented characters
/// match across input methods.</item>
/// <item>Length-prefixed serialization: 4-byte big-endian UTF-8 byte count
/// followed by the bytes. Prevents boundary-collision attacks where two
/// different field tuples concatenate to the same byte stream
/// ("Foo" + "Bar" vs "FooB" + "ar").</item>
/// </list>
public static class AdsolutCompanyHash
{
    /// Compute the SHA-256 hash of one company's mirrored field-set.
    /// Returns 32 raw bytes — store as <c>BYTEA</c>, never as a hex string.
    public static byte[] Compute(AdsolutCompanyHashInput input)
    {
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();

        // Stable order is part of the hash contract — never reorder, never
        // insert without bumping every stored hash. The order matches the
        // physical column order on `companies` so the SQL projection in the
        // upserter and push-tak can be eyeballed against it.
        Append(ms, Canonicalize(input.Name));
        Append(ms, Canonicalize(input.Code));
        Append(ms, Canonicalize(input.VatCombined));
        Append(ms, Canonicalize(input.AddressLine1));
        Append(ms, Canonicalize(input.AddressLine2));
        Append(ms, Canonicalize(input.PostalCode));
        Append(ms, Canonicalize(input.City));
        Append(ms, Canonicalize(input.Country));
        Append(ms, Canonicalize(input.Phone));
        Append(ms, CanonicalizeEmail(input.Email));

        ms.Position = 0;
        return sha.ComputeHash(ms);
    }

    private static string Canonicalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.Trim().Normalize(NormalizationForm.FormC);
    }

    private static string CanonicalizeEmail(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.Trim().ToLower(CultureInfo.InvariantCulture).Normalize(NormalizationForm.FormC);
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

/// Inputs for <see cref="AdsolutCompanyHash.Compute"/>. Captures the SD-side
/// shape (combined VAT, single phone field, two-line address) — both the
/// upserter and the push-tak project their respective sources into this
/// shape before hashing so the comparison is apples-to-apples.
public sealed record AdsolutCompanyHashInput(
    string? Name,
    string? Code,
    string? VatCombined,
    string? AddressLine1,
    string? AddressLine2,
    string? PostalCode,
    string? City,
    string? Country,
    string? Phone,
    string? Email);
