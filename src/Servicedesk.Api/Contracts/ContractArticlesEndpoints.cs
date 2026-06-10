using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Contracts;

/// Agent-facing endpoints for the Contract Articles module (Contracts →
/// Contract Articles): the mirrored Adsolut article catalogue list, a manual
/// "Sync articles" trigger (incremental — the worker only pulls what changed)
/// and a per-row resync.
///
/// Gated by the per-user <c>contracts_enabled</c> flag: the route policy is
/// RequireAgent (role gate) and every handler additionally verifies the flag
/// in-handler (Forbid on miss), so the gate is the real security boundary — not
/// just UI visibility — consistent with the Statistics feature.
public static class ContractArticlesEndpoints
{
    public static IEndpointRouteBuilder MapContractArticlesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/articles")
            .WithTags("Contracts")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("", ListArticles).WithName("ListAdsolutArticles").WithOpenApi();
        group.MapPost("/sync", TriggerSync).WithName("TriggerAdsolutArticlesSync").WithOpenApi();
        group.MapPost("/{id:guid}/resync", ResyncArticle).WithName("ResyncAdsolutArticle").WithOpenApi();

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

    private static async Task<IResult> ListArticles(
        string? search,
        int? page,
        int? pageSize,
        string? sort,
        string? dir,
        bool? activeOnly,
        HttpContext http,
        IUserService users,
        IAdsolutArticleRepository repo,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var result = await repo.ListAsync(search, page ?? 1, pageSize ?? 50, sort, dir, activeOnly ?? false, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(ToDto),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
        });
    }

    private static async Task<IResult> TriggerSync(
        HttpContext http,
        IUserService users,
        IAdsolutArticlesSyncSignal signal,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpArticlesEnabled, ct);
        if (!enabled)
        {
            return Results.BadRequest(new { error = "disabled", message = "Enable 'Pull articles' first." });
        }
        signal.RequestImmediateRun();
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_articles.sync_requested",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Accepted();
    }

    private static async Task<IResult> ResyncArticle(
        Guid id,
        HttpContext http,
        IUserService users,
        IAdsolutConnectionStore connections,
        IAdsolutArticlesClient client,
        IAdsolutArticleRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Adsolut is not connected or no dossier is active." });
        }

        var (actor, role) = ActorContext.Resolve(http);
        AdsolutArticle? full;
        try
        {
            full = await client.GetByIdAsync(administrationId, id, ct);
        }
        catch (AdsolutApiException ex)
        {
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        if (full is null)
        {
            return Results.NotFound(new { error = "not_found_upstream", message = "Adsolut returned no article for this id." });
        }

        await repo.UpsertAsync(full, ct);

        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_articles.resync_one",
            Actor: actor,
            ActorRole: role,
            Target: id.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { articleId = id }), ct);

        var row = await repo.GetByIdAsync(id, ct);
        return row is null ? Results.Ok(new { ok = true }) : Results.Ok(ToDto(row));
    }

    // ---- DTO mapping ----------------------------------------------------

    private static object ToDto(AdsolutArticleRow a) => new
    {
        id = a.Id,
        code = a.Code,
        name = a.Name,
        description = a.Description,
        vatCode = a.VatCode,
        vatRate = a.VatRate,
        active = a.Active,
        adsolutCreatedUtc = a.AdsolutCreatedUtc,
        adsolutLastModified = a.AdsolutLastModified,
        syncedUtc = a.SyncedUtc,
    };
}
