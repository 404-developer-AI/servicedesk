using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.30 — pins the decision behaviour the coverage page's "Force sync"
/// action relies on: bypassing the toggle-gates does NOT bypass the
/// hash-no-op or no-drift gates. A force-push of a row with no actual
/// drift returns a Skipped outcome and produces zero upstream traffic;
/// a force-push of a row with drift fires the PUT/POST. This is the
/// contract the coverage endpoint relies on when calling PushOneAsync
/// with synthetic <c>BothOn</c> options.
public sealed class AdsolutCoverageDecisionTests
{
    private static readonly AdsolutPushOptions BothOn =
        new(PushUpdateEnabled: true, PushCreateEnabled: true);

    [Fact]
    public void ForceSync_with_drift_and_differing_hash_returns_Updated()
    {
        var candidate = new AdsolutCompanyPushCandidate
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Code = "C-1",
            Email = "info@acme.example",
            Phone = "+32 2 000 00 00",
            AddressLine1 = "Loi 1",
            AddressLine2 = string.Empty,
            PostalCode = "1000",
            City = "Brussels",
            Country = "BE",
            VatNumber = "BE0123456789",
            AdsolutId = Guid.NewGuid(),
            AdsolutNumber = "1000",
            AdsolutAlphaCode = "ACME",
            AdsolutLastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedUtc = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc),
            // Stored hash differs from the freshly-computed hash → push.
            AdsolutSyncedHash = new byte[32],
        };
        var freshHash = AdsolutCompanyHash.Compute(new AdsolutCompanyHashInput(
            Name: candidate.Name, Code: candidate.Code, VatCombined: candidate.VatNumber,
            AddressLine1: candidate.AddressLine1, AddressLine2: candidate.AddressLine2,
            PostalCode: candidate.PostalCode, City: candidate.City, Country: candidate.Country,
            Phone: candidate.Phone, Email: candidate.Email));

        var d = AdsolutCompanyPusher.Decide(candidate, BothOn, freshHash);

        Assert.Equal(AdsolutPushOutcome.Updated, d.Outcome);
    }

    [Fact]
    public void ForceSync_with_no_drift_returns_SkippedNoLocalChange()
    {
        // updated_utc <= adsolut_last_modified — push-tak treats this as
        // "no SD-side edit since the last pull". The force-action should
        // still respect this so we do not generate unnecessary upstream
        // traffic.
        var ts = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var candidate = new AdsolutCompanyPushCandidate
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Code = "C-1",
            Email = "info@acme.example",
            VatNumber = "BE0123456789",
            AdsolutId = Guid.NewGuid(),
            AdsolutNumber = "1000",
            AdsolutLastModified = ts,
            UpdatedUtc = ts,
        };
        var freshHash = AdsolutCompanyHash.Compute(new AdsolutCompanyHashInput(
            Name: candidate.Name, Code: candidate.Code, VatCombined: candidate.VatNumber,
            AddressLine1: candidate.AddressLine1, AddressLine2: candidate.AddressLine2,
            PostalCode: candidate.PostalCode, City: candidate.City, Country: candidate.Country,
            Phone: candidate.Phone, Email: candidate.Email));

        var d = AdsolutCompanyPusher.Decide(candidate, BothOn, freshHash);

        Assert.Equal(AdsolutPushOutcome.SkippedNoLocalChange, d.Outcome);
    }

    [Fact]
    public void ForceSync_with_drift_but_equal_hash_returns_SkippedNoChange()
    {
        // Hash equal → echo-pull no-op even when timestamps drifted.
        var candidate = new AdsolutCompanyPushCandidate
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Code = "C-1",
            Email = "info@acme.example",
            VatNumber = "BE0123456789",
            AdsolutId = Guid.NewGuid(),
            AdsolutNumber = "1000",
            AdsolutLastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedUtc = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc),
        };
        var freshHash = AdsolutCompanyHash.Compute(new AdsolutCompanyHashInput(
            Name: candidate.Name, Code: candidate.Code, VatCombined: candidate.VatNumber,
            AddressLine1: candidate.AddressLine1, AddressLine2: candidate.AddressLine2,
            PostalCode: candidate.PostalCode, City: candidate.City, Country: candidate.Country,
            Phone: candidate.Phone, Email: candidate.Email));
        candidate.AdsolutSyncedHash = freshHash;

        var d = AdsolutCompanyPusher.Decide(candidate, BothOn, freshHash);

        Assert.Equal(AdsolutPushOutcome.SkippedNoChange, d.Outcome);
    }

    [Fact]
    public void Contact_ForceSync_with_drift_returns_Updated()
    {
        var candidate = new AdsolutContactPushCandidate
        {
            LinkId = Guid.NewGuid(),
            ContactId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            CompanyAdsolutId = Guid.NewGuid(),
            AdsolutContactId = Guid.NewGuid(),
            FirstName = "Wendy",
            LastName = "Test",
            Email = "wendy@example.com",
            Phone = "+32 2 000 00 00",
            MobilePhone = string.Empty,
            ContactUpdatedUtc = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc),
            AdsolutLastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            AdsolutSyncedHash = new byte[32],
        };
        var freshHash = AdsolutContactHash.Compute(new AdsolutContactHashInput(
            FirstName: candidate.FirstName,
            LastName: candidate.LastName,
            Phone: candidate.Phone,
            MobilePhone: candidate.MobilePhone));

        var d = AdsolutContactPusher.Decide(
            candidate,
            new AdsolutContactsPushOptions(true, true),
            freshHash);

        Assert.Equal(AdsolutContactPushOutcome.Updated, d.Outcome);
    }

    [Fact]
    public void Contact_ForceSync_with_no_drift_returns_SkippedNoLocalChange()
    {
        var ts = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var candidate = new AdsolutContactPushCandidate
        {
            LinkId = Guid.NewGuid(),
            ContactId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            CompanyAdsolutId = Guid.NewGuid(),
            AdsolutContactId = Guid.NewGuid(),
            FirstName = "Wendy",
            LastName = "Test",
            Email = "wendy@example.com",
            Phone = "+32 2 000 00 00",
            MobilePhone = string.Empty,
            ContactUpdatedUtc = ts,
            AdsolutLastModified = ts,
        };
        var freshHash = AdsolutContactHash.Compute(new AdsolutContactHashInput(
            FirstName: candidate.FirstName,
            LastName: candidate.LastName,
            Phone: candidate.Phone,
            MobilePhone: candidate.MobilePhone));

        var d = AdsolutContactPusher.Decide(
            candidate,
            new AdsolutContactsPushOptions(true, true),
            freshHash);

        Assert.Equal(AdsolutContactPushOutcome.SkippedNoLocalChange, d.Outcome);
    }
}
