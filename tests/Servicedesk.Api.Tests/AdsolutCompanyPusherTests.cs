using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.27 — pins the pure decision logic of the Adsolut→servicedesk
/// Companies push-tak. The SQL path + write-client are reviewer-trusted;
/// these tests guard the toggle-respect, drift-detection, and hash-no-op
/// rules that close the echo-pull loop. They run without a Postgres
/// connection or HTTP client.
public sealed class AdsolutCompanyPusherTests
{
    private static readonly AdsolutPushOptions BothOn =
        new(PushUpdateEnabled: true, PushCreateEnabled: true);
    private static readonly AdsolutPushOptions UpdateOnly =
        new(PushUpdateEnabled: true, PushCreateEnabled: false);
    private static readonly AdsolutPushOptions CreateOnly =
        new(PushUpdateEnabled: false, PushCreateEnabled: true);
    private static readonly AdsolutPushOptions BothOff =
        new(PushUpdateEnabled: false, PushCreateEnabled: false);

    private static AdsolutCompanyPushCandidate Candidate(
        Guid? adsolutId = null,
        DateTime? adsolutLastModified = null,
        DateTime? updatedUtc = null,
        byte[]? syncedHash = null,
        string name = "Acme",
        string? adsolutNumber = "1000") => new()
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = name,
            Code = "CUST-1",
            Email = "info@acme.example",
            Phone = "+32 2 123 45 67",
            AddressLine1 = "Rue de Loi 1",
            AddressLine2 = string.Empty,
            PostalCode = "1000",
            City = "Brussels",
            Country = "BE",
            VatNumber = "BE0123456789",
            AdsolutId = adsolutId,
            AdsolutNumber = adsolutNumber,
            AdsolutAlphaCode = "CUST-1",
            AdsolutLastModified = adsolutLastModified,
            AdsolutSyncedHash = syncedHash,
            UpdatedUtc = updatedUtc ?? new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
        };

    private static byte[] HashOf(AdsolutCompanyPushCandidate c) =>
        AdsolutCompanyHash.Compute(new AdsolutCompanyHashInput(
            Name: c.Name, Code: c.Code, VatCombined: c.VatNumber,
            AddressLine1: c.AddressLine1, AddressLine2: c.AddressLine2,
            PostalCode: c.PostalCode, City: c.City, Country: c.Country,
            Phone: c.Phone, Email: c.Email));

    [Fact]
    public void Unlinked_with_create_on_returns_Created()
    {
        var c = Candidate(adsolutId: null);
        var d = AdsolutCompanyPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutPushOutcome.Created, d.Outcome);
    }

    [Fact]
    public void Unlinked_with_create_off_returns_SkippedCreateToggleOff()
    {
        var c = Candidate(adsolutId: null);
        var d = AdsolutCompanyPusher.Decide(c, UpdateOnly, HashOf(c));

        Assert.Equal(AdsolutPushOutcome.SkippedCreateToggleOff, d.Outcome);
    }

    [Fact]
    public void Linked_with_no_local_drift_returns_SkippedNoLocalChange()
    {
        // updated_utc == adsolut_last_modified — typical state right after
        // a successful pull. The push-tak must not see this as drift.
        var ts = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var c = Candidate(
            adsolutId: Guid.NewGuid(),
            adsolutLastModified: ts,
            updatedUtc: ts);

        var d = AdsolutCompanyPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutPushOutcome.SkippedNoLocalChange, d.Outcome);
    }

    [Fact]
    public void Linked_with_local_drift_and_update_off_returns_SkippedUpdateToggleOff()
    {
        var c = Candidate(
            adsolutId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            updatedUtc: new DateTime(2026, 4, 29, 14, 0, 0, DateTimeKind.Utc));

        var d = AdsolutCompanyPusher.Decide(c, CreateOnly, HashOf(c));

        Assert.Equal(AdsolutPushOutcome.SkippedUpdateToggleOff, d.Outcome);
    }

    [Fact]
    public void Linked_with_drift_and_matching_hash_returns_SkippedNoChange()
    {
        // Loop-preventie: local row was updated_utc-bumped (e.g. by a
        // touch on a non-mirrored field) but the canonical hash equals
        // the last-synced hash. Push must not fire.
        var c = Candidate(
            adsolutId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            updatedUtc: new DateTime(2026, 4, 29, 14, 0, 0, DateTimeKind.Utc));
        c.AdsolutSyncedHash = HashOf(c);

        var d = AdsolutCompanyPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutPushOutcome.SkippedNoChange, d.Outcome);
    }

    [Fact]
    public void Linked_with_drift_and_differing_hash_returns_Updated()
    {
        var c = Candidate(
            adsolutId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            updatedUtc: new DateTime(2026, 4, 29, 14, 0, 0, DateTimeKind.Utc),
            syncedHash: new byte[32] /* zeros — definitely differs */);

        var d = AdsolutCompanyPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutPushOutcome.Updated, d.Outcome);
    }

    [Fact]
    public void Linked_with_null_synced_hash_treats_as_dirty()
    {
        // First push after upgrade — adsolut_synced_hash is NULL because
        // the column was added in v0.0.27 and the row was last touched
        // before that. We treat NULL as "definitely differs" so the push
        // fires once and stamps the hash for the next tick.
        var c = Candidate(
            adsolutId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            updatedUtc: new DateTime(2026, 4, 29, 14, 0, 0, DateTimeKind.Utc),
            syncedHash: null);

        var d = AdsolutCompanyPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutPushOutcome.Updated, d.Outcome);
    }

    [Fact]
    public void Linked_with_drift_but_no_adsolut_number_returns_SkippedMissingAdsolutNumber()
    {
        // Upgrade-from-v0.0.26 row: pulled before adsolut_number column
        // existed, never re-pulled. Pushing without klantnummer would hit
        // `UpdateCustomerNumberNotValid` from WK; gate skips until next
        // pull tick fills the column in.
        var c = Candidate(
            adsolutId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            updatedUtc: new DateTime(2026, 4, 29, 14, 0, 0, DateTimeKind.Utc),
            syncedHash: new byte[32],
            adsolutNumber: null);

        var d = AdsolutCompanyPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutPushOutcome.SkippedMissingAdsolutNumber, d.Outcome);
    }

    [Fact]
    public void Both_toggles_off_with_unlinked_returns_SkippedCreateToggleOff()
    {
        var c = Candidate(adsolutId: null);
        var d = AdsolutCompanyPusher.Decide(c, BothOff, HashOf(c));

        Assert.Equal(AdsolutPushOutcome.SkippedCreateToggleOff, d.Outcome);
    }

    // ---- Address splitting ------------------------------------------

    [Theory]
    [InlineData("Hoofdstraat 12", "Hoofdstraat", "12")]
    [InlineData("Rue de la Loi 1", "Rue de la Loi", "1")]
    [InlineData("Hoofdstraat 12B", "Hoofdstraat", "12B")]
    [InlineData("Some Avenue 100A", "Some Avenue", "100A")]
    public void SplitAddressLine1_pulls_trailing_number_token(
        string raw, string expectedStreet, string expectedNumber)
    {
        var (street, number) = AdsolutCompanyPusher.SplitAddressLine1(raw);
        Assert.Equal(expectedStreet, street);
        Assert.Equal(expectedNumber, number);
    }

    [Theory]
    [InlineData("Hoofdstraat", "Hoofdstraat", "")]
    [InlineData("", "", "")]
    [InlineData("   ", "", "")]
    [InlineData("1600 Pennsylvania Avenue", "1600 Pennsylvania Avenue", "")]
    public void SplitAddressLine1_keeps_full_string_when_no_trailing_digit(
        string raw, string expectedStreet, string expectedNumber)
    {
        var (street, number) = AdsolutCompanyPusher.SplitAddressLine1(raw);
        Assert.Equal(expectedStreet, street);
        Assert.Equal(expectedNumber, number);
    }

    // ---- Box-number stripping ---------------------------------------

    [Theory]
    [InlineData("Bus 12", "12")]
    [InlineData("bus 12", "12")]
    [InlineData("BUS 5A", "5A")]
    [InlineData("Box 7", "7")]
    [InlineData("12B", "12B")]
    [InlineData("", "")]
    public void ExtractBoxNumber_strips_known_prefixes(string raw, string expected)
    {
        Assert.Equal(expected, AdsolutCompanyPusher.ExtractBoxNumber(raw));
    }

    // ---- VAT splitting ----------------------------------------------

    [Theory]
    [InlineData("BE0123456789", "BE", "0123456789")]
    [InlineData("be0123456789", "BE", "0123456789")] // prefix uppercased
    [InlineData("NL123456789B01", "NL", "123456789B01")]
    [InlineData("0123456789", "", "0123456789")]
    [InlineData("", "", "")]
    [InlineData("BE", "BE", "")]
    public void SplitVat_pulls_two_letter_prefix(
        string raw, string expectedPrefix, string expectedDigits)
    {
        var (prefix, digits) = AdsolutCompanyPusher.SplitVat(raw);
        Assert.Equal(expectedPrefix, prefix);
        Assert.Equal(expectedDigits, digits);
    }

    // ---- Loop-stability ---------------------------------------------

    [Fact]
    public void Pull_then_push_is_a_no_op_via_hash()
    {
        // Simulates the round-trip: an Adsolut row arrives, the upserter
        // would hash the inbound shape and store that hash. On the next
        // push-tak, the local row hashes to the same value (no field
        // changed), so the push must skip.
        var c = Candidate(
            adsolutId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            updatedUtc: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var roundTripHash = HashOf(c);
        c.AdsolutSyncedHash = roundTripHash;

        var d = AdsolutCompanyPusher.Decide(c, BothOn, roundTripHash);

        // SkippedNoLocalChange wins over SkippedNoChange because we check
        // the timestamp gate first — but either outcome means the push
        // didn't fire, which is what closes the loop.
        Assert.True(
            d.Outcome == AdsolutPushOutcome.SkippedNoLocalChange ||
            d.Outcome == AdsolutPushOutcome.SkippedNoChange,
            $"Expected one of the skip outcomes, got {d.Outcome}");
    }
}
