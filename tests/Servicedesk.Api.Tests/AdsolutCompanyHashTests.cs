using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.27 — locks in the canonicalization rules of the SHA-256 hash that
/// drives the push/pull no-op guard. A regression here silently invalidates
/// every stored adsolut_synced_hash in production, so these tests are
/// deliberately strict: byte-for-byte equality, not "looks the same".
public sealed class AdsolutCompanyHashTests
{
    private static AdsolutCompanyHashInput SampleInput() => new(
        Name: "Acme NV",
        Code: "CUST-1",
        VatCombined: "BE0123456789",
        AddressLine1: "Rue de la Loi 1",
        AddressLine2: "Bus 12",
        PostalCode: "1000",
        City: "Brussels",
        Country: "BE",
        Phone: "+32 2 123 45 67",
        Email: "info@acme.example");

    [Fact]
    public void Hash_is_32_bytes()
    {
        var hash = AdsolutCompanyHash.Compute(SampleInput());

        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void Same_input_produces_same_hash()
    {
        var a = AdsolutCompanyHash.Compute(SampleInput());
        var b = AdsolutCompanyHash.Compute(SampleInput());

        Assert.Equal(a, b);
    }

    [Fact]
    public void Email_case_does_not_change_hash()
    {
        var lower = AdsolutCompanyHash.Compute(SampleInput() with { Email = "info@acme.example" });
        var upper = AdsolutCompanyHash.Compute(SampleInput() with { Email = "INFO@ACME.EXAMPLE" });

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Name_whitespace_does_not_change_hash()
    {
        var clean = AdsolutCompanyHash.Compute(SampleInput() with { Name = "Acme NV" });
        var padded = AdsolutCompanyHash.Compute(SampleInput() with { Name = "  Acme NV  " });

        Assert.Equal(clean, padded);
    }

    [Fact]
    public void Null_and_empty_field_hash_identically()
    {
        var emptyAddr = AdsolutCompanyHash.Compute(SampleInput() with { AddressLine2 = string.Empty });
        var nullAddr = AdsolutCompanyHash.Compute(SampleInput() with { AddressLine2 = null });

        Assert.Equal(emptyAddr, nullAddr);
    }

    [Fact]
    public void Different_name_produces_different_hash()
    {
        var a = AdsolutCompanyHash.Compute(SampleInput());
        var b = AdsolutCompanyHash.Compute(SampleInput() with { Name = "Acme BV" });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_vat_produces_different_hash()
    {
        var a = AdsolutCompanyHash.Compute(SampleInput());
        var b = AdsolutCompanyHash.Compute(SampleInput() with { VatCombined = "NL0123456789" });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_phone_produces_different_hash()
    {
        var a = AdsolutCompanyHash.Compute(SampleInput());
        var b = AdsolutCompanyHash.Compute(SampleInput() with { Phone = "+32 9 999 99 99" });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Length_prefixing_prevents_boundary_collision()
    {
        // Without length-prefixing, ("Foo", "Bar") and ("FooB", "ar") would
        // both serialize to "FooBar" and collide. Length headers separate
        // them so the hashes differ.
        var a = AdsolutCompanyHash.Compute(SampleInput() with { Name = "Foo", Code = "Bar" });
        var b = AdsolutCompanyHash.Compute(SampleInput() with { Name = "FooB", Code = "ar" });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Unicode_NFC_normalization_collapses_decomposed_form()
    {
        // "Café" in NFC vs NFD: the latter writes 'e' + combining acute (U+0301).
        // Both should hash identically after NFC normalization.
        var nfc = AdsolutCompanyHash.Compute(SampleInput() with { Name = "Café" });
        var nfd = AdsolutCompanyHash.Compute(SampleInput() with { Name = "Café" });

        Assert.Equal(nfc, nfd);
    }

    [Fact]
    public void All_null_input_still_produces_a_deterministic_hash()
    {
        var allNull = new AdsolutCompanyHashInput(
            Name: null, Code: null, VatCombined: null,
            AddressLine1: null, AddressLine2: null, PostalCode: null,
            City: null, Country: null, Phone: null, Email: null);

        var a = AdsolutCompanyHash.Compute(allNull);
        var b = AdsolutCompanyHash.Compute(allNull);

        Assert.Equal(a, b);
        Assert.Equal(32, a.Length);
    }
}
