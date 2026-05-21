using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Zammad;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Zammad migration link (v0.0.41 fase 1). Admin-only;
/// all routes live under <c>/api/admin/integrations/zammad</c>. Phase 1
/// only ships connectivity — token + base URL CRUD, status, test-connection
/// and an integration-audit reader. Ticket-picker, dry-run and import land
/// in fasen 2-5 against this same base path.
public static class ZammadEndpoints
{
    public static IEndpointRouteBuilder MapZammadEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/zammad")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", GetStatus).WithName("GetZammadStatus").WithOpenApi();

        // Token CRUD — mirrors the Telavox /secret surface. Value never
        // returned on read; only existence is exposed.
        admin.MapGet("/secret", GetSecretStatus).WithName("GetZammadSecretStatus").WithOpenApi();
        admin.MapPut("/secret", SetSecret).WithName("SetZammadSecret").WithOpenApi();
        admin.MapDelete("/secret", DeleteSecret).WithName("DeleteZammadSecret").WithOpenApi();

        // Base URL setter. Separate route so the audit-log can distinguish
        // a token change (security event) from a base-URL change (config
        // event). The matching value lives in the regular settings table
        // and is also editable via /api/settings/list?category=Zammad, but
        // surfacing a dedicated PUT lets us validate http(s) + host shape
        // before letting a typo land.
        admin.MapPut("/base-url", SetBaseUrl).WithName("SetZammadBaseUrl").WithOpenApi();

        // Test connection — fires /users/me + /version against the source
        // Zammad install. Returns the resolved agent + version + latency
        // so the SPA can render a "Connected as X (Zammad Y.Z.A) — XX ms"
        // status card.
        admin.MapPost("/test-connection", TestConnection).WithName("TestZammadConnection").WithOpenApi();

        // Picker support (fase 2). Groups + states are cached per admin
        // session on the SPA side; the search proxy composes Zammad's
        // ES-style query from structured filters so the picker UI stays
        // ignorant of the on-wire syntax. All three are gated on
        // Zammad.Enabled — flipping the master switch off freezes the
        // picker without needing to also clear the token.
        admin.MapGet("/groups", ListGroups).WithName("ListZammadGroups").WithOpenApi();
        admin.MapGet("/states", ListStates).WithName("ListZammadStates").WithOpenApi();
        admin.MapGet("/tickets/search", SearchTickets).WithName("SearchZammadTickets").WithOpenApi();
        admin.MapGet("/users/{userId:long}", GetUser).WithName("GetZammadUser").WithOpenApi();

        // integration_audit reader — reuses the shared IntegrationAuditLog
        // component on the SPA-side. Every Zammad API call writes one row;
        // first place to look when a Test connection fails or a future
        // import goes sideways.
        admin.MapGet("/audit", GetAuditLog).WithName("GetZammadAuditLog").WithOpenApi();

        return app;
    }

    // ---- /status --------------------------------------------------------

    private static async Task<IResult> GetStatus(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Zammad.Enabled, ct);
        var baseUrl = (await settings.GetAsync<string>(SettingKeys.Zammad.BaseUrl, ct) ?? string.Empty).Trim();
        var hasToken = await secrets.HasAsync(ProtectedSecretKeys.ZammadToken, ct);

        var state = ResolveState(enabled, hasToken, baseUrl);

        return Results.Ok(new
        {
            state = state.ToString(),
            enabled,
            tokenConfigured = hasToken,
            baseUrl = baseUrl.Length == 0 ? null : baseUrl,
        });
    }

    private static ZammadConnectionState ResolveState(
        bool enabled, bool hasToken, string baseUrl)
    {
        if (!enabled) return ZammadConnectionState.Disabled;
        if (!hasToken || baseUrl.Length == 0) return ZammadConnectionState.NotConfigured;
        return ZammadConnectionState.Ready;
    }

    // ---- /secret CRUD ---------------------------------------------------

    private static async Task<IResult> GetSecretStatus(
        IProtectedSecretStore secrets, CancellationToken ct) =>
        Results.Ok(new { configured = await secrets.HasAsync(ProtectedSecretKeys.ZammadToken, ct) });

    public sealed record SetSecretRequest([property: Required] string Value);

    private static async Task<IResult> SetSecret(
        [FromBody] SetSecretRequest req,
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Value))
            return Results.BadRequest(new { error = "missing_value", message = "API token is required." });

        var trimmed = req.Value.Trim();
        // Defensive length cap — Zammad personal-access tokens are short
        // opaque strings (32–64 chars in practice). A multi-megabyte paste
        // is almost certainly accidental clipboard content.
        if (trimmed.Length > 4096)
        {
            return Results.BadRequest(new
            {
                error = "invalid_token",
                message = "API token exceeds 4096 characters; check what you pasted.",
            });
        }

        await secrets.SetAsync(ProtectedSecretKeys.ZammadToken, trimmed, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.TokenUpdated,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.ZammadToken,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = true }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSecret(
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        await secrets.DeleteAsync(ProtectedSecretKeys.ZammadToken, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.TokenDeleted,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.ZammadToken,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = false }), ct);
        return Results.NoContent();
    }

    // ---- /base-url ------------------------------------------------------

    public sealed record SetBaseUrlRequest([property: Required] string BaseUrl);

    private static async Task<IResult> SetBaseUrl(
        [FromBody] SetBaseUrlRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.BaseUrl))
        {
            return Results.BadRequest(new
            {
                error = "missing_base_url",
                message = "Base URL is required (e.g. https://desk.example.com).",
            });
        }

        var trimmed = req.BaseUrl.Trim().TrimEnd('/');
        if (!TryValidateBaseUrl(trimmed, out var validationError))
        {
            return Results.BadRequest(new { error = "invalid_base_url", message = validationError });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Zammad.BaseUrl, trimmed, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.BaseUrlUpdated,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Zammad.BaseUrl,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { baseUrl = trimmed }), ct);
        return Results.Ok(new { baseUrl = trimmed });
    }

    /// Shape-gate on top of Uri.TryCreate. Rejects:
    /// <list type="bullet">
    /// <item>Anything that isn't http or https</item>
    /// <item>http on a non-localhost host (would route the API token
    /// over plaintext)</item>
    /// <item>Hosts longer than 255 chars (RFC 1035) or with non-DNS
    /// characters that hint at a paste mistake</item>
    /// </list>
    private static bool TryValidateBaseUrl(string raw, out string error)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            error = "Could not parse as an absolute URL. Use the full https://… form.";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "Scheme must be http or https.";
            return false;
        }
        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            error = "URL is missing a host.";
            return false;
        }
        if (host.Length > 255)
        {
            error = "Host is too long.";
            return false;
        }
        var isLocalhost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1";
        if (uri.Scheme == Uri.UriSchemeHttp && !isLocalhost)
        {
            error = "Only https is allowed for non-localhost hosts — http would route the API token over plaintext.";
            return false;
        }
        // Path/query/fragment on a base URL is almost always a paste
        // mistake (admin pasted a deep link). Refuse them gently so the
        // admin notices.
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            error = "Base URL must not include a path (the client appends /api/v1/... itself).";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Base URL must not include a query string or fragment.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    // ---- /test-connection ----------------------------------------------

    private static async Task<IResult> TestConnection(
        IZammadApiClient client,
        CancellationToken ct)
    {
        try
        {
            var result = await client.TestConnectionAsync(ct);
            return Results.Ok(new
            {
                me = new
                {
                    id = result.Me.Id,
                    email = result.Me.Email,
                    firstName = result.Me.FirstName,
                    lastName = result.Me.LastName,
                    login = result.Me.Login,
                },
                version = result.ZammadVersion,
                latencyMs = result.LatencyMs,
            });
        }
        catch (ZammadApiException ex)
        {
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }
    }

    // ---- picker endpoints -----------------------------------------------

    private static async Task<IResult> ListGroups(
        ISettingsService settings,
        IZammadApiClient client,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;

        try
        {
            var items = await client.ListGroupsAsync(ct);
            return Results.Ok(new
            {
                items = items.Select(g => new { id = g.Id, name = g.Name, active = g.Active }),
            });
        }
        catch (ZammadApiException ex)
        {
            return UpstreamError(ex);
        }
    }

    private static async Task<IResult> ListStates(
        ISettingsService settings,
        IZammadApiClient client,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;

        try
        {
            var items = await client.ListStatesAsync(ct);
            return Results.Ok(new
            {
                items = items.Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    stateTypeId = s.StateTypeId,
                    active = s.Active,
                }),
            });
        }
        catch (ZammadApiException ex)
        {
            return UpstreamError(ex);
        }
    }

    private static async Task<IResult> SearchTickets(
        ISettingsService settings,
        IZammadApiClient client,
        [FromQuery] string? q,
        [FromQuery(Name = "groupIds")] long[]? groupIds,
        [FromQuery(Name = "stateIds")] long[]? stateIds,
        [FromQuery] int? page,
        [FromQuery] int? perPage,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;

        var resolvedPerPage = perPage ?? await settings.GetAsync<int>(SettingKeys.Zammad.PerPageDefault, ct);
        if (resolvedPerPage <= 0) resolvedPerPage = 50;
        var query = new ZammadTicketSearchQuery(
            FreeText: q,
            GroupIds: groupIds ?? Array.Empty<long>(),
            StateIds: stateIds ?? Array.Empty<long>(),
            Page: Math.Max(page ?? 1, 1),
            PerPage: Math.Clamp(resolvedPerPage, 1, 200));

        try
        {
            var pageResult = await client.SearchTicketsAsync(query, ct);
            return Results.Ok(new
            {
                items = pageResult.Items.Select(t => new
                {
                    id = t.Id,
                    number = t.Number,
                    title = t.Title,
                    customerId = t.CustomerId,
                    customerEmail = t.CustomerEmail,
                    customerName = t.CustomerName,
                    groupId = t.GroupId,
                    groupName = t.GroupName,
                    stateId = t.StateId,
                    stateName = t.StateName,
                    articleCount = t.ArticleCount,
                    createdAt = t.CreatedAt,
                    updatedAt = t.UpdatedAt,
                }),
                total = pageResult.Total,
                page = pageResult.Page,
                perPage = pageResult.PerPage,
            });
        }
        catch (ZammadApiException ex)
        {
            return UpstreamError(ex);
        }
    }

    private static async Task<IResult> GetUser(
        long userId,
        ISettingsService settings,
        IZammadApiClient client,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;

        try
        {
            var user = await client.GetUserAsync(userId, ct);
            if (user is null) return Results.NotFound(new { error = "user_not_found" });
            return Results.Ok(new
            {
                id = user.Id,
                email = user.Email,
                firstName = user.FirstName,
                lastName = user.LastName,
                login = user.Login,
                organizationId = user.OrganizationId,
                active = user.Active,
            });
        }
        catch (ZammadApiException ex)
        {
            return UpstreamError(ex);
        }
    }

    /// Shared 502 envelope mirroring the Telavox endpoints.
    private static IResult UpstreamError(ZammadApiException ex) =>
        Results.Json(new
        {
            error = "upstream_error",
            httpStatus = ex.HttpStatus,
            upstreamErrorCode = ex.UpstreamErrorCode,
            message = ex.Message,
        }, statusCode: 502);

    /// Refuses with 409 when the master kill-switch is off. Used by every
    /// outbound-bound endpoint so flipping the toggle freezes the entire
    /// surface without leaving stale picker rows lit on the SPA.
    private static async Task<IResult?> EnabledGuard(
        ISettingsService settings, CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Zammad.Enabled, ct);
        if (enabled) return null;
        return Results.Json(new
        {
            error = "integration_disabled",
            message = "Zammad integration is disabled. Toggle it on under Behaviour first.",
        }, statusCode: 409);
    }

    // ---- /audit ---------------------------------------------------------

    private static async Task<IResult> GetAuditLog(
        IIntegrationAuditQuery audit,
        long? cursor,
        int? limit,
        CancellationToken ct)
    {
        var page = await audit.ListAsync(ZammadEventTypes.Integration, cursor, limit ?? 50, ct);
        return Results.Ok(new
        {
            items = page.Items.Select(e => new
            {
                id = e.Id,
                utc = e.Utc,
                eventType = e.EventType,
                outcome = e.Outcome,
                endpoint = e.Endpoint,
                httpStatus = e.HttpStatus,
                latencyMs = e.LatencyMs,
                actorId = e.ActorId,
                actorRole = e.ActorRole,
                errorCode = e.ErrorCode,
                payload = e.PayloadJson,
            }),
            nextCursor = page.NextCursor,
        });
    }
}
