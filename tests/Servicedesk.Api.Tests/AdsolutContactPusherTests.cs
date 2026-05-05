using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.29 — pins the pure decision logic of the SD→Adsolut Contacts
/// push-tak. The SQL path + write-client are reviewer-trusted; these tests
/// guard the toggle-respect, drift-detection, hash-no-op and email-empty
/// rules that close the echo-pull loop and enforce SD's CITEXT-required
/// email contract. Run without a Postgres connection or HTTP client.
public sealed class AdsolutContactPusherTests
{
    private static readonly AdsolutContactsPushOptions BothOn =
        new(PushUpdateEnabled: true, PushCreateEnabled: true);
    private static readonly AdsolutContactsPushOptions UpdateOnly =
        new(PushUpdateEnabled: true, PushCreateEnabled: false);
    private static readonly AdsolutContactsPushOptions CreateOnly =
        new(PushUpdateEnabled: false, PushCreateEnabled: true);
    private static readonly AdsolutContactsPushOptions BothOff =
        new(PushUpdateEnabled: false, PushCreateEnabled: false);

    private static AdsolutContactPushCandidate Candidate(
        Guid? adsolutContactId = null,
        DateTime? adsolutLastModified = null,
        DateTime? contactUpdatedUtc = null,
        byte[]? syncedHash = null,
        string firstName = "Wendy",
        string lastName = "De Smet",
        string email = "wendy@acme.example",
        string phone = "+32 2 111 22 33",
        string mobilePhone = "+32 470 11 22 33") => new()
        {
            LinkId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ContactId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            CompanyId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            CompanyAdsolutId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            AdsolutContactId = adsolutContactId,
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            Phone = phone,
            MobilePhone = mobilePhone,
            ContactUpdatedUtc = contactUpdatedUtc ?? new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            AdsolutLastModified = adsolutLastModified,
            AdsolutSyncedHash = syncedHash,
        };

    private static byte[] HashOf(AdsolutContactPushCandidate c) =>
        AdsolutContactHash.Compute(new AdsolutContactHashInput(
            FirstName: c.FirstName,
            LastName: c.LastName,
            Phone: c.Phone,
            MobilePhone: c.MobilePhone));

    [Fact]
    public void Unlinked_with_create_on_returns_Created()
    {
        var c = Candidate(adsolutContactId: null);
        var d = AdsolutContactPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.Created, d.Outcome);
    }

    [Fact]
    public void Unlinked_with_create_off_returns_SkippedCreateToggleOff()
    {
        var c = Candidate(adsolutContactId: null);
        var d = AdsolutContactPusher.Decide(c, UpdateOnly, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.SkippedCreateToggleOff, d.Outcome);
    }

    [Fact]
    public void Linked_with_no_local_drift_returns_SkippedNoLocalChange()
    {
        // contact.updated_utc == link.adsolut_last_modified — typical state
        // right after a successful pull. The push-tak must not see this
        // as drift.
        var ts = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var c = Candidate(
            adsolutContactId: Guid.NewGuid(),
            adsolutLastModified: ts,
            contactUpdatedUtc: ts);

        var d = AdsolutContactPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.SkippedNoLocalChange, d.Outcome);
    }

    [Fact]
    public void Linked_with_local_drift_and_update_off_returns_SkippedUpdateToggleOff()
    {
        var c = Candidate(
            adsolutContactId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            contactUpdatedUtc: new DateTime(2026, 5, 5, 14, 0, 0, DateTimeKind.Utc));

        var d = AdsolutContactPusher.Decide(c, CreateOnly, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.SkippedUpdateToggleOff, d.Outcome);
    }

    [Fact]
    public void Linked_with_drift_and_matching_hash_returns_SkippedNoChange()
    {
        // Loop-prevention: the contact's updated_utc was bumped (e.g. an
        // edit on a non-mirrored field) but the canonical hash equals the
        // last-synced hash. Push must not fire.
        var c = Candidate(
            adsolutContactId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            contactUpdatedUtc: new DateTime(2026, 5, 5, 14, 0, 0, DateTimeKind.Utc));
        c.AdsolutSyncedHash = HashOf(c);

        var d = AdsolutContactPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.SkippedNoChange, d.Outcome);
    }

    [Fact]
    public void Linked_with_drift_and_differing_hash_returns_Updated()
    {
        var c = Candidate(
            adsolutContactId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            contactUpdatedUtc: new DateTime(2026, 5, 5, 14, 0, 0, DateTimeKind.Utc),
            syncedHash: new byte[32] /* zeros — definitely differs */);

        var d = AdsolutContactPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.Updated, d.Outcome);
    }

    [Fact]
    public void Linked_with_null_synced_hash_treats_as_dirty()
    {
        // First push after upgrade — adsolut_synced_hash is NULL because
        // the row was last touched before v0.0.28 added the column. We
        // treat NULL as "definitely differs" so the push fires once and
        // stamps the hash for the next tick.
        var c = Candidate(
            adsolutContactId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            contactUpdatedUtc: new DateTime(2026, 5, 5, 14, 0, 0, DateTimeKind.Utc),
            syncedHash: null);

        var d = AdsolutContactPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.Updated, d.Outcome);
    }

    [Fact]
    public void Empty_email_returns_SkippedNoEmail()
    {
        // SD's contacts schema has email CITEXT NOT NULL UNIQUE — a row
        // without an email shouldn't reach this gate, but a defensive
        // guard means a future schema change can't silently produce a
        // 400 from Adsolut.
        var c = Candidate(adsolutContactId: Guid.NewGuid(), email: string.Empty);

        var d = AdsolutContactPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.SkippedNoEmail, d.Outcome);
    }

    [Fact]
    public void Whitespace_only_email_returns_SkippedNoEmail()
    {
        var c = Candidate(adsolutContactId: Guid.NewGuid(), email: "   \t  ");

        var d = AdsolutContactPusher.Decide(c, BothOn, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.SkippedNoEmail, d.Outcome);
    }

    [Fact]
    public void Both_toggles_off_with_unlinked_returns_SkippedCreateToggleOff()
    {
        var c = Candidate(adsolutContactId: null);
        var d = AdsolutContactPusher.Decide(c, BothOff, HashOf(c));

        Assert.Equal(AdsolutContactPushOutcome.SkippedCreateToggleOff, d.Outcome);
    }

    // ---- Loop-stability ---------------------------------------------

    [Fact]
    public void Pull_then_push_is_a_no_op_via_hash()
    {
        // Round-trip: an Adsolut contact arrives, the upserter hashes the
        // four mirror fields and stores that hash on the link. On the next
        // push-tak the local row hashes to the same value (no field
        // changed), so the push must skip — either via the timestamp gate
        // or the hash gate. Either outcome closes the loop.
        var c = Candidate(
            adsolutContactId: Guid.NewGuid(),
            adsolutLastModified: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            contactUpdatedUtc: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var roundTripHash = HashOf(c);
        c.AdsolutSyncedHash = roundTripHash;

        var d = AdsolutContactPusher.Decide(c, BothOn, roundTripHash);

        Assert.True(
            d.Outcome == AdsolutContactPushOutcome.SkippedNoLocalChange ||
            d.Outcome == AdsolutContactPushOutcome.SkippedNoChange,
            $"Expected one of the skip outcomes, got {d.Outcome}");
    }
}
