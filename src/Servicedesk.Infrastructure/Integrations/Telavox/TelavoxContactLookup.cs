using Servicedesk.Domain.Companies;

namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// The answer to a Telavox contact lookup: only what the call widget shows.
/// Deliberately narrow — no e-mail, no ids, no tickets.
public sealed record TelavoxContactLookupResult(string Name, string Company)
{
    /// Name and company in one string ("Jan Peeters · Acme"). A Telavox
    /// webhook stores one variable and its JSONPath picks one field, so
    /// this lets a single webhook fill a single widget row.
    public string Label => string.Join(" · ", new[] { Name, Company }.Where(s => s.Length > 0));
}

/// Pure projection from the phone-match rows to the lookup answer
/// (v0.1.35). One contact → their name + best company link. Several
/// contacts on one number (a switchboard, a shared line) → we don't guess
/// a person: "Multiple contacts" plus the company when they all share one.
public static class TelavoxContactLookup
{
    public const string MultipleContacts = "Multiple contacts";

    public static TelavoxContactLookupResult? Resolve(IReadOnlyList<ContactPhoneLookupRow> rows)
    {
        if (rows.Count == 0) return null;

        if (rows.Count == 1)
        {
            var c = rows[0];
            var name = $"{c.FirstName} {c.LastName}".Trim();
            return new TelavoxContactLookupResult(name, c.LinkedCompanyName?.Trim() ?? string.Empty);
        }

        var companies = rows
            .Select(r => r.LinkedCompanyName?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shared = companies.Count == 1 ? companies[0] : string.Empty;
        return new TelavoxContactLookupResult(MultipleContacts, shared);
    }
}

/// In-memory counters for the admin panel — the lookup route writes no row
/// per call (a ringing phone is not an audit event); denied calls are
/// audited separately. Reset on restart.
public sealed class TelavoxContactLookupStats
{
    private long _found;
    private long _notFound;
    private long _denied;
    private long _lastLookupTicks;

    public void RecordFound(DateTime utcNow) { Interlocked.Increment(ref _found); Touch(utcNow); }
    public void RecordNotFound(DateTime utcNow) { Interlocked.Increment(ref _notFound); Touch(utcNow); }
    public void RecordDenied() => Interlocked.Increment(ref _denied);

    public (long Found, long NotFound, long Denied, DateTime? LastLookupUtc) Snapshot()
    {
        var ticks = Interlocked.Read(ref _lastLookupTicks);
        return (Interlocked.Read(ref _found), Interlocked.Read(ref _notFound), Interlocked.Read(ref _denied),
            ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc));
    }

    private void Touch(DateTime utcNow) => Interlocked.Exchange(ref _lastLookupTicks, utcNow.Ticks);
}
