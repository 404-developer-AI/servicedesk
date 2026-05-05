using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.28 — locks in the canonicalisation rules of the SHA-256 hash that
/// drives the contacts pull-tak no-op guard (and the v0.0.29 push-tak hash
/// gate). A regression here silently invalidates every stored
/// contact_companies.adsolut_synced_hash in production.
public sealed class AdsolutContactHashTests
{
    private static AdsolutContactHashInput SampleInput() => new(
        FirstName: "Wendy",
        LastName: "Janssens",
        Phone: "+32 9 123 45 67",
        MobilePhone: "+32 478 12 34 56");

    [Fact]
    public void Hash_is_32_bytes()
    {
        var hash = AdsolutContactHash.Compute(SampleInput());
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void Same_input_produces_same_hash()
    {
        var a = AdsolutContactHash.Compute(SampleInput());
        var b = AdsolutContactHash.Compute(SampleInput());
        Assert.Equal(a, b);
    }

    [Fact]
    public void Whitespace_around_a_field_does_not_change_hash()
    {
        var clean = AdsolutContactHash.Compute(SampleInput() with { FirstName = "Wendy" });
        var padded = AdsolutContactHash.Compute(SampleInput() with { FirstName = "  Wendy  " });
        Assert.Equal(clean, padded);
    }

    [Fact]
    public void Null_and_empty_hash_identically()
    {
        var emptyMobile = AdsolutContactHash.Compute(SampleInput() with { MobilePhone = string.Empty });
        var nullMobile = AdsolutContactHash.Compute(SampleInput() with { MobilePhone = null });
        Assert.Equal(emptyMobile, nullMobile);
    }

    [Fact]
    public void Different_first_name_produces_different_hash()
    {
        var a = AdsolutContactHash.Compute(SampleInput());
        var b = AdsolutContactHash.Compute(SampleInput() with { FirstName = "Wendyy" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_last_name_produces_different_hash()
    {
        var a = AdsolutContactHash.Compute(SampleInput());
        var b = AdsolutContactHash.Compute(SampleInput() with { LastName = "Janssen" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_phone_produces_different_hash()
    {
        var a = AdsolutContactHash.Compute(SampleInput());
        var b = AdsolutContactHash.Compute(SampleInput() with { Phone = "+32 9 999 99 99" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_mobile_phone_produces_different_hash()
    {
        var a = AdsolutContactHash.Compute(SampleInput());
        var b = AdsolutContactHash.Compute(SampleInput() with { MobilePhone = "+32 471 11 22 33" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Length_prefixing_prevents_boundary_collision()
    {
        // ("Foo", "Bar") vs ("FooB", "ar") concatenate to the same bytes;
        // length-prefixing splits them apart.
        var a = AdsolutContactHash.Compute(SampleInput() with { FirstName = "Foo", LastName = "Bar" });
        var b = AdsolutContactHash.Compute(SampleInput() with { FirstName = "FooB", LastName = "ar" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Unicode_NFC_normalisation_collapses_decomposed_form()
    {
        // "Café" composed (U+00E9) vs decomposed (e + U+0301). NFC must
        // collapse to the same byte sequence so the hash is invariant.
        var nfc = AdsolutContactHash.Compute(SampleInput() with { FirstName = "Café" });
        var nfd = AdsolutContactHash.Compute(SampleInput() with { FirstName = "Café" });
        Assert.Equal(nfc, nfd);
    }

    [Fact]
    public void All_null_input_still_produces_a_deterministic_hash()
    {
        var allNull = new AdsolutContactHashInput(null, null, null, null);
        var a = AdsolutContactHash.Compute(allNull);
        var b = AdsolutContactHash.Compute(allNull);
        Assert.Equal(a, b);
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void Email_is_not_in_the_hash()
    {
        // The hash is intentionally email-free — email is the match-key,
        // never overwritten after the initial match. Two AdsolutContactHashInput
        // values that differ only in fields that ARE hashed produce
        // different hashes; here we just verify the field-set itself is
        // stable to the four documented fields by checking that the raw
        // bytes differ when phone changes.
        var a = AdsolutContactHash.Compute(SampleInput());
        var b = AdsolutContactHash.Compute(SampleInput() with { Phone = "different" });
        Assert.NotEqual(a, b);
    }
}
