using System.Security.Cryptography;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Integrations;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Integrations.M365;
using Servicedesk.Infrastructure.Secrets;
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

        // Per-company connect lifecycle + the synced mailbox view.
        group.MapGet("/companies/connections", ListConnections).WithName("ListM365Connections").WithOpenApi();
        group.MapPost("/companies/{companyId:guid}/connect", Connect).WithName("ConnectM365Company").WithOpenApi();
        group.MapPost("/companies/{companyId:guid}/sync", SyncNow).WithName("SyncM365Company").WithOpenApi();
        group.MapPost("/companies/{companyId:guid}/disconnect", Disconnect).WithName("DisconnectM365Company").WithOpenApi();
        group.MapGet("/companies/{companyId:guid}/mailboxes", GetMailboxes).WithName("GetM365CompanyMailboxes").WithOpenApi();

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

    // ---- per-company connect lifecycle ---------------------------------

    /// Connection status per company (status badge + "last synced" copy).
    /// Keyed by companyId so the frontend merges it into the companies list.
    private static async Task<IResult> ListConnections(
        HttpContext http,
        IUserService users,
        IM365TenantStore store,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var rows = await store.GetConnectionsAsync(ct);
        return Results.Ok(new
        {
            items = rows.Select(r => new
            {
                companyId = r.CompanyId,
                status = r.Status,
                consentedAt = r.ConsentedAt,
                lastCheckedUtc = r.LastCheckedUtc,
                lastChangedUtc = r.LastChangedUtc,
                mailboxCount = r.MailboxCount,
                lastError = r.LastError,
            }),
        });
    }

    /// Mint a single-use, server-time-boxed state and return the variable
    /// admin-consent URL. The browser opens it; the customer's global admin
    /// signs in and consents; Azure redirects to the public callback which
    /// validates the state and stores the tenant link.
    private static async Task<IResult> Connect(
        Guid companyId,
        HttpContext http,
        IUserService users,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IM365TenantStore store,
        IIntegrationAuditLogger audit,
        CancellationToken ct)
    {
        var (userId, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        // The shared MSP app must be set up first (Settings → Integrations →
        // Microsoft 365). Without a client id the consent URL is meaningless.
        var enabled = await settings.GetAsync<bool>(SettingKeys.M365.Enabled, ct);
        var clientId = (await settings.GetAsync<string>(SettingKeys.M365.ClientId, ct) ?? string.Empty).Trim();
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.M365ClientSecret, ct);
        if (!enabled || clientId.Length == 0 || !hasSecret)
        {
            return Results.BadRequest(new
            {
                error = "not_configured",
                message = "Configure and enable the Microsoft 365 integration (Settings → Integrations → Microsoft 365) before connecting a customer.",
            });
        }

        var ttlMinutes = Math.Clamp(await settings.GetAsync<int>(SettingKeys.M365.ConsentLinkTtlMinutes, ct), 5, 120);
        var state = NewStateToken();
        await store.CreateConsentStateAsync(
            state, companyId, userId == Guid.Empty ? null : userId,
            DateTime.UtcNow.AddMinutes(ttlMinutes), ct);

        var publicBase = await M365Endpoints.ResolvePublicBaseAsync(http, settings, ct);
        var redirectUri = publicBase + M365Endpoints.ConsentCallbackPath;

        // organizations (not common) — work/school tenants only, no personal
        // accounts. Azure resolves the tenant from whoever signs in.
        var consentUrl =
            "https://login.microsoftonline.com/organizations/adminconsent" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString(state)}";

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new IntegrationAuditEvent(
            Integration: M365EventTypes.Integration,
            EventType: M365EventTypes.ConsentStarted,
            Outcome: IntegrationAuditOutcome.Ok,
            ActorId: actor,
            ActorRole: role,
            Payload: new { companyId, ttlMinutes }), ct);

        return Results.Ok(new { consentUrl });
    }

    /// On-demand re-read for one connected company ("Sync now").
    private static async Task<IResult> SyncNow(
        Guid companyId,
        HttpContext http,
        IUserService users,
        IM365SyncService sync,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var outcome = await sync.SyncCompanyAsync(companyId, ct);
        return Results.Ok(new
        {
            success = outcome.Success,
            status = outcome.Status,
            error = outcome.Error,
            mailboxCount = outcome.MailboxCount,
            changed = outcome.Changed,
        });
    }

    /// Forget a company's link locally (clears link + mailboxes + sync state).
    /// Full revocation is the customer removing the enterprise app in their
    /// tenant; this is the Servicedesk-side disconnect.
    private static async Task<IResult> Disconnect(
        Guid companyId,
        HttpContext http,
        IUserService users,
        IM365TenantStore store,
        IIntegrationAuditLogger audit,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        await store.DeleteLinkAsync(companyId, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new IntegrationAuditEvent(
            Integration: M365EventTypes.Integration,
            EventType: M365EventTypes.Disconnected,
            Outcome: IntegrationAuditOutcome.Ok,
            ActorId: actor,
            ActorRole: role,
            Payload: new { companyId }), ct);
        return Results.Ok(new { ok = true });
    }

    /// The synced mailbox view for one company (status + sync meta + rows).
    private static async Task<IResult> GetMailboxes(
        Guid companyId,
        HttpContext http,
        IUserService users,
        IM365TenantStore store,
        CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var label = await store.GetCompanyLabelAsync(companyId, ct);
        var link = await store.GetLinkAsync(companyId, ct);
        var syncState = await store.GetSyncStateAsync(companyId, ct);
        var mailboxes = await store.GetMailboxesAsync(companyId, ct);

        return Results.Ok(new
        {
            companyId,
            companyCode = label?.Code,
            companyName = label?.Name,
            status = link?.Status,
            tenantId = link?.TenantId,
            consentedAt = link?.ConsentedAt,
            lastCheckedUtc = syncState?.LastCheckedUtc,
            lastChangedUtc = syncState?.LastChangedUtc,
            lastError = link?.LastError ?? syncState?.LastError,
            mailboxes = mailboxes.Select(m => new
            {
                objectId = m.ObjectId,
                mailboxType = m.MailboxType,
                displayName = m.DisplayName,
                givenName = m.GivenName,
                surname = m.Surname,
                upn = m.Upn,
                mail = m.Mail,
                enabled = m.Enabled,
                licenses = m.Licenses,
            }),
        });
    }

    /// 32 random bytes, URL-safe base64 — unguessable single-use consent token.
    private static string NewStateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private sealed record M365SelectionRequest(string[]? ArticleIds);
}
