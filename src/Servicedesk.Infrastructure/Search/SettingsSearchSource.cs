using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// In-memory index over the Settings pages. Admin-only: the hard guard in
/// <see cref="IsAvailableFor"/> plus the explicit re-check in
/// <see cref="SearchAsync"/> ensure a non-admin principal can never see
/// settings results, even if a caller bypasses the façade's filtering.
public sealed class SettingsSearchSource : ISearchSource
{
    public string Kind => SearchSourceKind.Settings;

    public bool IsAvailableFor(SearchPrincipal principal) => principal.IsAdmin;

    private static readonly IReadOnlyList<SettingsEntry> Entries = new List<SettingsEntry>
    {
        new("security", "Beveiliging", "/settings/security",
            "rate limit hsts csp password argon2 lockout session cookie 2fa totp recovery"),
        new("mail", "Mail & Graph", "/settings/mail",
            "mail polling graph tenant client mailbox plus address processed folder"),
        new("storage", "Opslag & bijlagen", "/settings/storage",
            "blob root attachments retention inline image disk warn critical mailbox cap"),
        new("tickets", "Tickets", "/settings/tickets",
            "tickets default priority page size notification queue column layout"),
        new("sla", "SLA", "/settings/sla",
            "sla business hours holidays first response resolution policy pause pending dashboard recalc"),
        new("jobs", "Jobs & retentie", "/settings/jobs",
            "jobs retention attachment worker concurrency poll attempts backoff dead letter"),
        new("navigation", "Navigatie", "/settings/navigation",
            "navigation sidebar open tickets link"),
        new("queues", "Queues", "/settings/queues",
            "queue management inbound outbound mailbox address routing"),
        new("views", "Views", "/settings/views",
            "views saved filter groups visibility sharing"),
        new("users", "Gebruikers", "/settings/users",
            "users agents admins role queue access permissions"),
        new("audit", "Audit log", "/settings/audit",
            "audit log events actor role target history"),
        new("health", "Health", "/settings/health",
            "health status incidents blob disk mail graph observability"),
    }.AsReadOnly();

    public Task<SearchGroup> SearchAsync(
        SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return Task.FromResult(new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false));

        var normalized = request.Query.Trim().ToLowerInvariant();
        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        var matches = Entries
            .Select(e => new
            {
                Entry = e,
                Score = ScoreEntry(e, normalized),
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Title)
            .ToList();

        var page = matches.Skip(offset).Take(limit).ToList();
        var hits = page.Select(x => new SearchHit(
            Kind: Kind,
            EntityId: x.Entry.Id,
            Title: x.Entry.Title,
            Snippet: null,
            Rank: x.Score,
            Meta: new Dictionary<string, string?> { ["path"] = x.Entry.Path })).ToList();

        var hasMore = matches.Count > offset + hits.Count;
        return Task.FromResult(new SearchGroup(Kind, hits, matches.Count, hasMore));
    }

    private static double ScoreEntry(SettingsEntry entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var title = entry.Title.ToLowerInvariant();
        var keywords = entry.Keywords.ToLowerInvariant();

        if (title == query) return 100;
        if (title.StartsWith(query)) return 50;
        if (title.Contains(query)) return 25;
        if (keywords.Contains(query)) return 10;

        // Tokenized fallback: any query word matches a keyword word.
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = words.Count(w => title.Contains(w) || keywords.Contains(w));
        return hits > 0 ? hits : 0;
    }

    private sealed record SettingsEntry(string Id, string Title, string Path, string Keywords);
}
