using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Contracts;

/// Agent-facing endpoints for the Microsoft 365 matching module (Contracts →
/// Microsoft 365 matching): read/save which Adsolut articles count as "M365
/// related" (the gear on the page) and list the companies whose contracts
/// reference any of those articles — each company once.
///
/// Gated by the per-user <c>contracts_enabled</c> flag exactly like the other
/// Contracts modules: the route policy is RequireAgent (role gate) and every
/// handler additionally verifies the flag in-handler (Forbid on miss), so the
/// gate is the real security boundary — not just UI visibility.
public static class ContractM365Endpoints
{
    public static IEndpointRouteBuilder MapContractM365Endpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/m365")
            .WithTags("Contracts")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/selection", GetSelection).WithName("GetM365ArticleSelection").WithOpenApi();
        group.MapPut("/selection", SaveSelection).WithName("SaveM365ArticleSelection").WithOpenApi();
        group.MapGet("/companies", ListCompanies).WithName("ListM365MatchingCompanies").WithOpenApi();

        return group;
    }

    /// Resolve the caller + verify the contracts_enabled flag. Returns the
    /// user id on success, or an IResult (Unauthorized/Forbid) to short-circuit.
    private static async Task<(Guid UserId, IResult? Deny)> RequireContractsFlagAsync(
        HttpContext http, IUserService users, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        if (userId == Guid.Empty) return (Guid.Empty, Results.Unauthorized());
        if (!await users.GetContractsEnabledAsync(userId, ct)) return (Guid.Empty, Results.Forbid());
        return (userId, null);
    }

    /// Parse a comma-separated uuid list into distinct Guids, dropping blanks
    /// and anything that isn't a valid uuid (defensive: the setting is normally
    /// only written by SaveSelection, but never trust stored text).
    private static List<Guid> ParseIds(IEnumerable<string?>? raw)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        if (raw is null) return ids;
        foreach (var part in raw)
        {
            if (Guid.TryParse(part, out var id) && seen.Add(id)) ids.Add(id);
        }
        return ids;
    }

    private static List<Guid> ParseCsv(string? csv) =>
        ParseIds(csv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static async Task<IResult> GetSelection(
        HttpContext http,
        IUserService users,
        ISettingsService settings,
        IAdsolutArticleRepository articles,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var ids = ParseCsv(await settings.GetAsync<string>(SettingKeys.Adsolut.M365MatchArticleIds, ct));
        var rows = await articles.GetByIdsAsync(ids, ct);
        return Results.Ok(new
        {
            articles = rows.Select(a => new { id = a.Id, code = a.Code, name = a.Name }),
        });
    }

    private static async Task<IResult> SaveSelection(
        M365SelectionRequest body,
        HttpContext http,
        IUserService users,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var ids = ParseIds(body.ArticleIds);
        if (ids.Count > 1000)
        {
            return Results.BadRequest(new { error = "too_many", message = "Too many articles selected." });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Adsolut.M365MatchArticleIds, string.Join(",", ids), actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: "contracts.m365.selection_saved",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { count = ids.Count }), ct);

        return Results.Ok(new { ok = true, count = ids.Count });
    }

    private static async Task<IResult> ListCompanies(
        HttpContext http,
        IUserService users,
        ISettingsService settings,
        IAdsolutContractRepository contracts,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var ids = ParseCsv(await settings.GetAsync<string>(SettingKeys.Adsolut.M365MatchArticleIds, ct));
        var rows = await contracts.GetM365CompaniesAsync(ids, ct);
        return Results.Ok(new
        {
            items = rows.Select(r => new
            {
                companyId = r.CompanyId,
                code = r.CompanyCode,
                name = r.CompanyName,
            }),
        });
    }

    private sealed record M365SelectionRequest(string[]? ArticleIds);
}
