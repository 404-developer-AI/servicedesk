using Servicedesk.Infrastructure.Phones;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.34 — pins the contract of the static E.164 normaliser the
/// contact-write paths and the call-popup phone-search rely on. Any
/// regression here would silently break "look up a contact by phone
/// number" the moment a row gets re-saved, so the parser is locked in
/// for the BE/NL formats most install agents will paste.
public sealed class PhoneE164Tests
{
    [Theory]
    [InlineData("+32498123456",     "BE", "+32498123456")]
    [InlineData("0498 12 34 56",    "BE", "+32498123456")]
    [InlineData("0498-12-34-56",    "BE", "+32498123456")]
    [InlineData("0498.12.34.56",    "BE", "+32498123456")]
    [InlineData("+32 (0)498 123456","BE", "+32498123456")]
    [InlineData("06 12 34 56 78",   "NL", "+31612345678")]
    [InlineData("0612345678",       "NL", "+31612345678")]
    [InlineData("+1 (212) 555-1234","BE", "+12125551234")]
    public void Valid_numbers_normalize_to_E164(string raw, string region, string expected)
    {
        Assert.True(PhoneE164.TryNormalize(raw, region, out var e164));
        Assert.Equal(expected, e164);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("john@example.com")]
    [InlineData("12")]
    [InlineData("0498-INVALID")]
    public void Invalid_input_returns_empty(string? raw)
    {
        Assert.False(PhoneE164.TryNormalize(raw, "BE", out var e164));
        Assert.Equal(string.Empty, e164);
    }

    [Fact]
    public void Normalize_helper_returns_E164_for_valid_input()
    {
        Assert.Equal("+32498123456", PhoneE164.Normalize("0498 12 34 56", "BE"));
    }

    [Fact]
    public void Normalize_helper_returns_empty_for_invalid_input()
    {
        Assert.Equal(string.Empty, PhoneE164.Normalize("not a number", "BE"));
    }

    [Fact]
    public void International_prefix_overrides_default_region()
    {
        // +33 NL-default → still parses as French; the leading + wins over
        // the default region. This is libphonenumber semantics but we pin
        // it as a fixture so a future swap to another lib doesn't quietly
        // change behaviour.
        Assert.True(PhoneE164.TryNormalize("+33 1 23 45 67 89", "NL", out var e164));
        Assert.Equal("+33123456789", e164);
    }
}
