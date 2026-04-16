using System.Security.Claims;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Access;
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
            ISettingsService settings,
            CancellationToken ct) =>
        {
            var principal = await BuildPrincipalAsync(http, queueAccess, ct);
            var minLen = await settings.GetAsync<int>(SettingKeys.Search.MinQueryLength, ct);
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
                });
            }

            var results = await search.SearchAsync(
                new SearchRequest(query, Type: null, Limit: capped, Offset: 0),
                principal, ct);

            return Results.Ok(new
            {
                groups = results.Groups,
                totalHits = results.TotalHits,
                availableKinds = search.AvailableKindsFor(principal),
                minQueryLength = minLen,
            });
        }).WithName("GlobalSearch").WithOpenApi();

        // Full-page search: one source, paginated.
        group.MapGet("/full", async (
            string? q, string? type, int? limit, int? offset,
            HttpContext http,
            ISearchService search,
            IQueueAccessService queueAccess,
            ISettingsService settings,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(type))
                return Results.BadRequest(new { error = "type is required." });

            var principal = await BuildPrincipalAsync(http, queueAccess, ct);
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

            var results = await search.SearchAsync(
                new SearchRequest(query, Type: type, Limit: capped, Offset: safeOffset),
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

    private static async Task<SearchPrincipal> BuildPrincipalAsync(
        HttpContext http, IQueueAccessService queueAccess, CancellationToken ct)
    {
        var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
        IReadOnlyList<Guid>? allowed = null;
        if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            allowed = await queueAccess.GetAccessibleQueueIdsAsync(userId, role, ct);
        return new SearchPrincipal(userId, role, allowed);
    }
}
