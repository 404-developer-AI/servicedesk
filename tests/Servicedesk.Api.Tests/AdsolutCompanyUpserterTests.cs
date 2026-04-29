using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.26 — pins the pure decision logic of the Adsolut→servicedesk
/// Companies upserter. The SQL path is reviewer-trusted; these tests
/// guard the match-precedence, conflict tie-breaker, and toggle-respect
/// rules from regressions. They run without a Postgres connection.
public sealed class AdsolutCompanyUpserterTests
{
    private static readonly AdsolutSyncOptions BothOn = new(PullUpdateEnabled: true, PullCreateEnabled: true);
    private static readonly AdsolutSyncOptions UpdateOnly = new(PullUpdateEnabled: true, PullCreateEnabled: false);
    private static readonly AdsolutSyncOptions CreateOnly = new(PullUpdateEnabled: false, PullCreateEnabled: true);

    private static AdsolutCustomer Customer(
        DateTimeOffset? lastModified = null,
        string? code = "CUST-1") =>
        new(
            Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name: "Acme",
            Code: code,
            AlphaCode: code,
            Number: "1000",
            Email: "acme@example.com",
            Phone: "+32 2 123 45 67",
            MobilePhone: "",
            AddressLine1: "Rue de Loi 1",
            AddressLine2: "",
            PostalCode: "1000",
            City: "Brussels",
            Country: "BE",
            VatNumber: "0123456789",
            CountryPrefixVatNumber: "BE",
            LastModified: lastModified);

    [Fact]
    public void No_match_with_create_on_returns_Created()
    {
        var d = AdsolutCompanyUpserter.Decide(match: null, Customer(), BothOn, inboundHash: Array.Empty<byte>());

        Assert.Equal(AdsolutUpsertOutcome.Created, d.Outcome);
        Assert.Null(d.Match);
    }

    [Fact]
    public void No_match_with_create_off_returns_SkippedCreateToggleOff()
    {
        var d = AdsolutCompanyUpserter.Decide(match: null, Customer(), UpdateOnly, inboundHash: Array.Empty<byte>());

        Assert.Equal(AdsolutUpsertOutcome.SkippedCreateToggleOff, d.Outcome);
    }

    [Fact]
    public void Match_with_update_on_and_no_local_newer_clock_returns_Updated()
    {
        var match = new AdsolutCompanyMatchRow(
            Id: Guid.NewGuid(),
            UpdatedUtc: new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            AdsolutId: null);
        var customer = Customer(lastModified: new DateTimeOffset(2026, 4, 28, 12, 0, 0, TimeSpan.Zero));

        var d = AdsolutCompanyUpserter.Decide(match, customer, BothOn, inboundHash: Array.Empty<byte>());

        Assert.Equal(AdsolutUpsertOutcome.Updated, d.Outcome);
        Assert.Same(match, d.Match);
    }

    [Fact]
    public void Match_with_update_off_returns_SkippedUpdateToggleOff()
    {
        var match = new AdsolutCompanyMatchRow(
            Id: Guid.NewGuid(),
            UpdatedUtc: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            AdsolutId: null);
        var customer = Customer(lastModified: new DateTimeOffset(2026, 4, 28, 0, 0, 0, TimeSpan.Zero));

        var d = AdsolutCompanyUpserter.Decide(match, customer, CreateOnly, inboundHash: Array.Empty<byte>());

        Assert.Equal(AdsolutUpsertOutcome.SkippedUpdateToggleOff, d.Outcome);
    }

    [Fact]
    public void Local_newer_than_adsolut_skips_with_SkippedLocalNewer_even_when_update_on()
    {
        // Local row was edited yesterday at 14:00; Adsolut row's lastModified
        // is yesterday at 12:00. Last-write-wins says local wins.
        var match = new AdsolutCompanyMatchRow(
            Id: Guid.NewGuid(),
            UpdatedUtc: new DateTime(2026, 4, 28, 14, 0, 0, DateTimeKind.Utc),
            AdsolutId: null);
        var customer = Customer(lastModified: new DateTimeOffset(2026, 4, 28, 12, 0, 0, TimeSpan.Zero));

        var d = AdsolutCompanyUpserter.Decide(match, customer, BothOn, inboundHash: Array.Empty<byte>());

        Assert.Equal(AdsolutUpsertOutcome.SkippedLocalNewer, d.Outcome);
    }

    [Fact]
    public void Adsolut_newer_than_local_returns_Updated_when_update_on()
    {
        // Symmetric to the above: Adsolut moved more recently than local.
        var match = new AdsolutCompanyMatchRow(
            Id: Guid.NewGuid(),
            UpdatedUtc: new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc),
            AdsolutId: null);
        var customer = Customer(lastModified: new DateTimeOffset(2026, 4, 28, 14, 0, 0, TimeSpan.Zero));

        var d = AdsolutCompanyUpserter.Decide(match, customer, BothOn, inboundHash: Array.Empty<byte>());

        Assert.Equal(AdsolutUpsertOutcome.Updated, d.Outcome);
    }

    [Fact]
    public void Match_without_lastModified_falls_through_to_Updated()
    {
        // Adsolut row didn't carry a lastModified (older API rows). With no
        // upstream timestamp to compare, the conflict tie-breaker can't
        // declare local-as-winner — we apply the upstream row.
        var match = new AdsolutCompanyMatchRow(
            Id: Guid.NewGuid(),
            UpdatedUtc: new DateTime(2026, 4, 28, 14, 0, 0, DateTimeKind.Utc),
            AdsolutId: null);
        var customer = Customer(lastModified: null);

        var d = AdsolutCompanyUpserter.Decide(match, customer, BothOn, inboundHash: Array.Empty<byte>());

        Assert.Equal(AdsolutUpsertOutcome.Updated, d.Outcome);
    }

    // ---- ExtractLinkableDomain ---------------------------------------

    private static readonly HashSet<string> Freemail = new(
        new[] { "gmail.com", "outlook.com" }, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("@nodomain.com")]
    [InlineData("foo@")]
    [InlineData("foo@bar")]          // no dot in host
    [InlineData("foo@.com")]         // empty label before dot
    [InlineData("foo@bar.")]         // trailing dot
    [InlineData("foo@bar baz.com")]  // space in domain
    public void Malformed_or_empty_emails_yield_null(string? email)
    {
        Assert.Null(AdsolutCompanyUpserter.ExtractLinkableDomain(email, blacklist: null));
    }

    [Theory]
    [InlineData("info@acme.com", "acme.com")]
    [InlineData("INFO@AcMe.CoM", "acme.com")]
    [InlineData("plus+tag@example.co.uk", "example.co.uk")]
    [InlineData("first.last@sub.domain.org", "sub.domain.org")]
    public void Valid_emails_yield_lowercased_domain(string email, string expected)
    {
        Assert.Equal(expected, AdsolutCompanyUpserter.ExtractLinkableDomain(email, blacklist: null));
    }

    [Fact]
    public void Freemail_blacklist_skips_listed_domains()
    {
        Assert.Null(AdsolutCompanyUpserter.ExtractLinkableDomain("user@gmail.com", Freemail));
        Assert.Null(AdsolutCompanyUpserter.ExtractLinkableDomain("user@OUTLOOK.com", Freemail));
        Assert.Equal("acme.com",
            AdsolutCompanyUpserter.ExtractLinkableDomain("user@acme.com", Freemail));
    }

    [Fact]
    public void Match_with_matching_inbound_hash_skips_with_SkippedNoChange()
    {
        // v0.0.27 inbound no-op guard: the canonical hash of the inbound
        // row equals the last-synced hash on the local row. The upserter
        // must NOT issue an UPDATE — that closes the echo-pull half of
        // the bidirectional loop.
        var hash = new byte[32];
        for (var i = 0; i < hash.Length; i++) hash[i] = (byte)i;
        var match = new AdsolutCompanyMatchRow(
            Id: Guid.NewGuid(),
            UpdatedUtc: new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc),
            AdsolutId: Guid.NewGuid(),
            AdsolutSyncedHash: hash);
        var customer = Customer(lastModified: new DateTimeOffset(2026, 4, 28, 14, 0, 0, TimeSpan.Zero));

        var d = AdsolutCompanyUpserter.Decide(match, customer, BothOn, inboundHash: hash);

        Assert.Equal(AdsolutUpsertOutcome.SkippedNoChange, d.Outcome);
    }

    [Fact]
    public void Match_with_differing_inbound_hash_falls_through_to_Updated()
    {
        var stored = new byte[32];
        var inbound = new byte[32];
        inbound[0] = 0x01; // differs by one byte
        var match = new AdsolutCompanyMatchRow(
            Id: Guid.NewGuid(),
            UpdatedUtc: new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc),
            AdsolutId: Guid.NewGuid(),
            AdsolutSyncedHash: stored);
        var customer = Customer(lastModified: new DateTimeOffset(2026, 4, 28, 14, 0, 0, TimeSpan.Zero));

        var d = AdsolutCompanyUpserter.Decide(match, customer, BothOn, inboundHash: inbound);

        Assert.Equal(AdsolutUpsertOutcome.Updated, d.Outcome);
    }

    [Fact]
    public void Match_with_null_synced_hash_treats_as_dirty()
    {
        // First pull after the v0.0.27 column was added — adsolut_synced_hash
        // is NULL because the column was added in v0.0.27 and the row was
        // last touched before that. Treat NULL as "definitely differs" so
        // the upserter fires once and stamps the hash for the next tick.
        var inbound = new byte[32];
        var match = new AdsolutCompanyMatchRow(
            Id: Guid.NewGuid(),
            UpdatedUtc: new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc),
            AdsolutId: Guid.NewGuid(),
            AdsolutSyncedHash: null);
        var customer = Customer(lastModified: new DateTimeOffset(2026, 4, 28, 14, 0, 0, TimeSpan.Zero));

        var d = AdsolutCompanyUpserter.Decide(match, customer, BothOn, inboundHash: inbound);

        Assert.Equal(AdsolutUpsertOutcome.Updated, d.Outcome);
    }

    [Fact]
    public void Equal_clocks_resolve_to_Updated_so_a_replay_is_idempotent()
    {
        // Same timestamp on both sides — neither side strictly wins. Last-
        // write-wins must NOT skip in that case (otherwise a re-run of the
        // same delta-pass would silently drop rows whose UTC timestamp the
        // previous run wrote into companies.updated_utc verbatim).
        var ts = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
        var match = new AdsolutCompanyMatchRow(
            Id: Guid.NewGuid(),
            UpdatedUtc: ts,
            AdsolutId: null);
        var customer = Customer(lastModified: new DateTimeOffset(ts, TimeSpan.Zero));

        var d = AdsolutCompanyUpserter.Decide(match, customer, BothOn, inboundHash: Array.Empty<byte>());

        Assert.Equal(AdsolutUpsertOutcome.Updated, d.Outcome);
    }
}
