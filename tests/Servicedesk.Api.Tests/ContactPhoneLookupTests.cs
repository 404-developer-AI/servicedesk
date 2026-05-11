using Servicedesk.Infrastructure.Phones;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.34 commit D — pins the phone-normalisation contract that the
/// /api/contacts/lookup-by-phone endpoint depends on. The Dapper-backed
/// repo method does an indexed equality match on phone_e164, so a stable
/// canonical form is the whole game: a national-format input from CAPI
/// ("0498123456") and an international-format stored contact ("+32498123456")
/// must collapse to the same key, otherwise the call-popup misses matches.
public sealed class ContactPhoneLookupTests
{
    [Fact]
    public void Normalises_belgian_national_format_to_e164()
    {
        Assert.True(PhoneE164.TryNormalize("0498123456", "BE", out var e164));
        Assert.Equal("+32498123456", e164);
    }

    [Fact]
    public void International_input_is_idempotent()
    {
        // CAPI delivers an already-E.164 "From" number for some PBX setups;
        // normalising it again must not produce a different string.
        Assert.True(PhoneE164.TryNormalize("+32498123456", "BE", out var first));
        Assert.True(PhoneE164.TryNormalize(first, "BE", out var second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Spaces_dashes_and_parentheses_are_stripped()
    {
        // Contacts pasted from Outlook / vCards arrive in many shapes — the
        // canonical column must not depend on which shape the admin typed.
        Assert.True(PhoneE164.TryNormalize("+32 (0)498-12 34 56", "BE", out var e164));
        Assert.Equal("+32498123456", e164);
    }

    [Fact]
    public void Unparseable_input_returns_empty()
    {
        // Endpoint contract: empty E.164 means "do not match" — the call
        // popup shows "Unknown caller" instead of running an open SQL match
        // that could bleed across contacts.
        Assert.False(PhoneE164.TryNormalize("not-a-number", "BE", out var e164));
        Assert.Equal(string.Empty, e164);
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.False(PhoneE164.TryNormalize(null, "BE", out var fromNull));
        Assert.False(PhoneE164.TryNormalize("", "BE", out var fromEmpty));
        Assert.False(PhoneE164.TryNormalize("   ", "BE", out var fromBlank));
        Assert.Equal(string.Empty, fromNull);
        Assert.Equal(string.Empty, fromEmpty);
        Assert.Equal(string.Empty, fromBlank);
    }

    [Fact]
    public void Region_aware_parsing_uses_supplied_default()
    {
        // Same digit string parses differently per region — the install-
        // wide DefaultCountryCode setting is the single source of truth.
        // A NL-region install ("06" mobile prefix) must not pick up "0498…"
        // as a NL number — libphonenumber rejects it as invalid.
        Assert.True(PhoneE164.TryNormalize("0612345678", "NL", out var nl));
        Assert.StartsWith("+31", nl);

        Assert.True(PhoneE164.TryNormalize("0498123456", "BE", out var be));
        Assert.StartsWith("+32", be);
    }
}
