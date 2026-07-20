using System.Security.Claims;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Search;

/// Global + full-page search endpoints. Gated by <c>RequireAgent</c> so
/// Customer gets 403 (the customer portal ships a scoped search together
/// with the Companies/Users feature in a later version).
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search")
            .WithTags("Search")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // Dropdown: top-N per available source.
        group.MapGet("/", async (
            string? q, int? limit,
            HttpContext http,
            ISearchService search,
            IQueueAccessService queueAccess,
            IUserService users,
            ISettingsService settings,
            CancellationToken ct) =>
        {
            var principal = await BuildPrincipalAsync(http, queueAccess, users, ct);
            var minLen = await settings.GetAsync<int>(SettingKeys.Search.MinQueryLength, ct);
            var debounceMs = await settings.GetAsync<int>(SettingKeys.Search.DebounceMs, ct);
            var capped = Math.Clamp(limit ?? 8, 1, 25);

            var query = (q ?? string.Empty).Trim();
            if (query.Length < minLen)
            {
                return Results.Ok(new
                {
                    groups = Array.Empty<object>(),
                    totalHits = 0,
                    availableKinds = search.AvailableKindsFor(principal),
                    minQueryLength = minLen,
                    debounceMs,
                });
            }

            var quickKinds = await GetQuickKindsAsync(settings, ct);
            var results = await search.SearchAsync(
                new SearchRequest(query, Type: null, Limit: capped, Offset: 0,
                    Kinds: quickKinds, QuickMode: true),
                principal, ct);

            return Results.Ok(new
            {
                groups = results.Groups,
                totalHits = results.TotalHits,
                availableKinds = search.AvailableKindsFor(principal),
                minQueryLength = minLen,
                debounceMs,
            });
        }).WithName("GlobalSearch").WithOpenApi();

        // Full-page search: one source, paginated.
        group.MapGet("/full", async (
            string? q, string? type, int? limit, int? offset, string? sort,
            HttpContext http,
            ISearchService search,
            IQueueAccessService queueAccess,
            IUserService users,
            ISettingsService settings,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(type))
                return Results.BadRequest(new { error = "type is required." });

            var principal = await BuildPrincipalAsync(http, queueAccess, users, ct);
            var minLen = await settings.GetAsync<int>(SettingKeys.Search.MinQueryLength, ct);
            var query = (q ?? string.Empty).Trim();
            if (query.Length < minLen)
            {
                return Results.Ok(new
                {
                    group = new SearchGroup(type!, Array.Empty<SearchHit>(), 0, false),
                    availableKinds = search.AvailableKindsFor(principal),
                    minQueryLength = minLen,
                });
            }

            var available = search.AvailableKindsFor(principal);
            if (!available.Contains(type!, StringComparer.OrdinalIgnoreCase))
                return Results.Forbid();

            var capped = Math.Clamp(limit ?? 25, 1, 100);
            var safeOffset = Math.Max(0, offset ?? 0);
            // Unknown sort values quietly fall back to relevance — the enum is
            // the whitelist, sources never see the raw string.
            var parsedSort = Enum.TryParse<SearchSort>(sort, ignoreCase: true, out var s)
                ? s : SearchSort.Relevance;

            var results = await search.SearchAsync(
                new SearchRequest(query, Type: type, Limit: capped, Offset: safeOffset,
                    Sort: parsedSort),
                principal, ct);

            var firstGroup = results.Groups.Count > 0
                ? results.Groups[0]
                : new SearchGroup(type!, Array.Empty<SearchHit>(), 0, false);

            return Results.Ok(new
            {
                group = firstGroup,
                availableKinds = available,
                minQueryLength = minLen,
            });
        }).WithName("GlobalSearchFull").WithOpenApi();

        return app;
    }

    /// Parses Search.QuickSources into the dropdown's source scope. An empty
    /// or whitespace-only value falls back to the default trio rather than
    /// "all sources" — reintroducing the all-source fan-out silently is
    /// exactly the slowness this setting exists to prevent.
    private static async Task<IReadOnlyList<string>> GetQuickKindsAsync(
        ISettingsService settings, CancellationToken ct)
    {
        var raw = await settings.GetAsync<string>(SettingKeys.Search.QuickSources, ct);
        var kinds = (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return kinds.Count > 0
            ? kinds
            : new[] { SearchSourceKind.Tickets, SearchSourceKind.Companies, SearchSourceKind.Contacts };
    }

    private static async Task<SearchPrincipal> BuildPrincipalAsync(
        HttpContext http, IQueueAccessService queueAccess, IUserService users, CancellationToken ct)
    {
        var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

        // Admins bypass both queue scoping and feature gating — no need to
        // hit the DB for either.
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return new SearchPrincipal(userId, role, allowedQueueIds: null);

        var allowed = await queueAccess.GetAccessibleQueueIdsAsync(userId, role, ct);
        var features = await users.GetSearchFeatureFlagsAsync(userId, ct);
        return new SearchPrincipal(userId, role, allowed, features);
    }
}
