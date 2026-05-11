using System.ComponentModel.DataAnnotations;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Telavox;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Telavox call-popup integration. Admin-gated; all
/// routes live under /api/admin/integrations/telavox. Two surfaces:
/// <list type="bullet">
/// <item>Configuration — partner-token CRUD, customer-id pin,
/// test-connection.</item>
/// <item>Per-agent provisioning — list extensions, list current
/// agent-links, link/unlink one SD user to one Telavox extension.</item>
/// </list>
/// Endpoints follow the Adsolut pattern: TelavoxApiException maps to a
/// 502 envelope; <see cref="InvalidOperationException"/> (configuration
/// or user-state errors) maps to 400 with a structured error code.
public static class TelavoxEndpoints
{
    public static IEndpointRouteBuilder MapTelavoxEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/telavox")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", GetStatus).WithName("GetTelavoxStatus").WithOpenApi();

        // Partner-token CRUD. Mirrors the Adsolut /secret surface — only
        // existence is reported back, never the value.
        admin.MapGet("/secret", GetSecretStatus).WithName("GetTelavoxSecretStatus").WithOpenApi();
        admin.MapPut("/secret", SetSecret).WithName("SetTelavoxSecret").WithOpenApi();
        admin.MapDelete("/secret", DeleteSecret).WithName("DeleteTelavoxSecret").WithOpenApi();

        // Customer-id pin. PUT sets a value picked from the test-connection
        // dropdown; DELETE clears it (integration falls back to "Test
        // connection to pick a customer" state).
        admin.MapPut("/customer", SetCustomerId).WithName("SetTelavoxCustomerId").WithOpenApi();
        admin.MapDelete("/customer", ClearCustomerId).WithName("ClearTelavoxCustomerId").WithOpenApi();

        // Diagnostics — verifies the partner-token and lists customers /
        // extensions the admin can pin and link against.
        admin.MapPost("/test-connection", TestConnection).WithName("TestTelavoxConnection").WithOpenApi();
        admin.MapGet("/extensions", ListExtensions).WithName("ListTelavoxExtensions").WithOpenApi();

        // Per-agent provisioning.
        admin.MapGet("/agents", ListAgentLinks).WithName("ListTelavoxAgentLinks").WithOpenApi();
        admin.MapPost("/agents/{userId:guid}/provision", ProvisionAgent)
            .WithName("ProvisionTelavoxAgent").WithOpenApi();
        admin.MapDelete("/agents/{userId:guid}/provision", RevokeAgent)
            .WithName("RevokeTelavoxAgent").WithOpenApi();

        return app;
    }

    // ---- /status --------------------------------------------------------

    private static async Task<IResult> GetStatus(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        ITelavoxAgentLinkStore links,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Telavox.Enabled, ct);
        var customerId = (await settings.GetAsync<string>(SettingKeys.Telavox.PartnerCustomerId, ct) ?? string.Empty).Trim();
        var hasToken = await secrets.HasAsync(ProtectedSecretKeys.TelavoxPartnerToken, ct);
        var linkRows = await links.ListAsync(ct);

        var state = ResolveState(enabled, hasToken, customerId, linkRows.Count);

        return Results.Ok(new
        {
            state = state.ToString(),
            enabled,
            partnerTokenConfigured = hasToken,
            partnerCustomerId = customerId.Length == 0 ? null : customerId,
            linkedAgentCount = linkRows.Count,
        });
    }

    private static TelavoxConnectionState ResolveState(
        bool enabled, bool hasToken, string customerId, int linkedAgents)
    {
        if (!enabled) return TelavoxConnectionState.Disabled;
        if (!hasToken) return TelavoxConnectionState.NotConfigured;
        if (customerId.Length == 0) return TelavoxConnectionState.NoCustomerSelected;
        return linkedAgents == 0
            ? TelavoxConnectionState.Ready
            : TelavoxConnectionState.Active;
    }

    // ---- /secret CRUD ---------------------------------------------------

    private static async Task<IResult> GetSecretStatus(
        IProtectedSecretStore secrets, CancellationToken ct) =>
        Results.Ok(new { configured = await secrets.HasAsync(ProtectedSecretKeys.TelavoxPartnerToken, ct) });

    public sealed record SetSecretRequest([property: Required] string Value);

    private static async Task<IResult> SetSecret(
        [FromBody] SetSecretRequest req,
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Value))
            return Results.BadRequest(new { error = "missing_value", message = "Partner token is required." });
        await secrets.SetAsync(ProtectedSecretKeys.TelavoxPartnerToken, req.Value.Trim(), ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: TelavoxEventTypes.PartnerTokenUpdated,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.TelavoxPartnerToken,
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
        await secrets.DeleteAsync(ProtectedSecretKeys.TelavoxPartnerToken, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: TelavoxEventTypes.PartnerTokenDeleted,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.TelavoxPartnerToken,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = false }), ct);
        return Results.NoContent();
    }

    // ---- /customer ------------------------------------------------------

    public sealed record SetCustomerIdRequest([property: Required] string CustomerId);

    private static async Task<IResult> SetCustomerId(
        [FromBody] SetCustomerIdRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.CustomerId))
            return Results.BadRequest(new { error = "missing_customer_id" });
        // Length cap and a conservative charset gate so a paste of "{ ... ;
        // DROP" can't slip into the settings table. Telavox customer-ids
        // are short opaque tokens in practice (numeric or alphanumeric).
        var trimmed = req.CustomerId.Trim();
        if (trimmed.Length > 64 || !IsAllowedIdentifierShape(trimmed))
        {
            return Results.BadRequest(new
            {
                error = "invalid_customer_id",
                message = "Customer ID must be 1–64 characters of letters, digits, '-', '_' or '.'.",
            });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Telavox.PartnerCustomerId, trimmed, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: TelavoxEventTypes.CustomerIdSelected,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Telavox.PartnerCustomerId,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { customerId = trimmed }), ct);
        return Results.Ok(new { customerId = trimmed });
    }

    private static async Task<IResult> ClearCustomerId(
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Telavox.PartnerCustomerId, string.Empty, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: TelavoxEventTypes.CustomerIdSelected,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Telavox.PartnerCustomerId,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { customerId = string.Empty }), ct);
        return Results.NoContent();
    }


    // ---- /test-connection ----------------------------------------------

    private static async Task<IResult> TestConnection(
        ITelavoxProvisioningService provisioning,
        CancellationToken ct)
    {
        try
        {
            var result = await provisioning.TestConnectionAsync(ct);
            return Results.Ok(new
            {
                customers = result.Customers.Select(c => new { id = c.Id, name = c.Name }),
            });
        }
        catch (TelavoxApiException ex)
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

    private static async Task<IResult> ListExtensions(
        ISettingsService settings,
        ITelavoxApiClient client,
        CancellationToken ct)
    {
        var customerId = (await settings.GetAsync<string>(SettingKeys.Telavox.PartnerCustomerId, ct) ?? string.Empty).Trim();
        if (customerId.Length == 0)
        {
            return Results.BadRequest(new
            {
                error = "no_customer_selected",
                message = "Pin a customer-id from the test-connection dropdown first.",
            });
        }
        try
        {
            var items = await client.ListExtensionsAsync(customerId, ct);
            return Results.Ok(new
            {
                customerId,
                items = items.Select(e => new
                {
                    id = e.Id,
                    number = e.Number,
                    name = e.Name,
                    userEmail = e.UserEmail,
                }),
            });
        }
        catch (TelavoxApiException ex)
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

    // ---- /agents --------------------------------------------------------

    private static async Task<IResult> ListAgentLinks(
        ITelavoxAgentLinkStore links,
        [FromServices] Npgsql.NpgsqlDataSource dataSource,
        CancellationToken ct)
    {
        // Two-step read so the SPA doesn't have to chase a second
        // endpoint to resolve user-emails for the per-row chip. Dapper
        // join would couple link-store SQL to the users table; the
        // shape is small (admin-only, bounded by agent count) so a
        // post-join in C# is the simpler trade-off.
        var rows = await links.ListAsync(ct);
        if (rows.Count == 0)
        {
            return Results.Ok(new { items = Array.Empty<object>() });
        }

        var userIds = rows.Select(r => r.UserId).ToArray();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        const string sql = """
            SELECT id          AS "Id",
                   email       AS "Email",
                   role_name   AS "RoleName"
              FROM users
             WHERE id = ANY(@Ids)
            """;
        var emails = (await conn.QueryAsync<UserRowDto>(new CommandDefinition(
                sql, new { Ids = userIds }, cancellationToken: ct)))
            .ToDictionary(u => u.Id, u => (u.Email, u.RoleName));

        return Results.Ok(new
        {
            items = rows.Select(r =>
            {
                emails.TryGetValue(r.UserId, out var ur);
                return new
                {
                    userId = r.UserId,
                    userEmail = ur.Email,
                    userRole = ur.RoleName,
                    telavoxExtension = r.TelavoxExtension,
                    telavoxUserId = r.TelavoxUserId,
                    capiUserEmail = r.CapiUserEmail,
                    provisionedUtc = r.ProvisionedUtc,
                    lastPollUtc = r.LastPollUtc,
                    lastPollError = r.LastPollError,
                    consecutiveErrors = r.ConsecutiveErrors,
                };
            }),
        });
    }

    public sealed record ProvisionAgentBody([property: Required] string Extension);

    private static async Task<IResult> ProvisionAgent(
        Guid userId,
        [FromBody] ProvisionAgentBody body,
        HttpContext http,
        ITelavoxProvisioningService provisioning,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Extension))
            return Results.BadRequest(new { error = "missing_extension" });
        // Cap + charset gate on the extension string. Telavox extensions
        // are short — numeric digits or a short label — so a long paste
        // is almost certainly user error.
        var trimmed = body.Extension.Trim();
        if (trimmed.Length > 64 || !IsAllowedIdentifierShape(trimmed))
        {
            return Results.BadRequest(new
            {
                error = "invalid_extension",
                message = "Extension must be 1–64 characters of letters, digits, '-', '_' or '.'.",
            });
        }

        var (actor, role) = ActorContext.Resolve(http);
        try
        {
            var link = await provisioning.ProvisionAgentAsync(
                new TelavoxProvisionAgentRequest(userId, trimmed, actor, role), ct);
            return Results.Ok(new
            {
                userId = link.UserId,
                telavoxExtension = link.TelavoxExtension,
                telavoxUserId = link.TelavoxUserId,
                capiUserEmail = link.CapiUserEmail,
                provisionedUtc = link.ProvisionedUtc,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = "provision_blocked", message = ex.Message });
        }
        catch (TelavoxApiException ex)
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

    private static async Task<IResult> RevokeAgent(
        Guid userId,
        HttpContext http,
        ITelavoxProvisioningService provisioning,
        CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        try
        {
            await provisioning.RevokeAgentAsync(userId, actor, role, ct);
            return Results.NoContent();
        }
        catch (TelavoxApiException ex)
        {
            // The provisioning service swallows upstream PAPI failures during
            // revoke (best-effort) and keeps the local cleanup running. If we
            // still see an exception here it came from outside the swallowed
            // path — surface it as 502 so the admin knows the local row was
            // not removed.
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }
    }

    /// Charset gate shared by the <c>PartnerCustomerId</c> setter and the
    /// per-agent <c>Extension</c> body. Both fields are stored verbatim
    /// and later URL-segment-encoded into PAPI paths
    /// (<c>/customers/{id}/api-users/{email}</c>); restricting to a
    /// conservative letters+digits+<c>-_.</c> alphabet keeps the upstream
    /// URL trivially safe and blocks paste-mistakes containing slashes,
    /// quotes or whitespace before they reach Telavox.
    private static bool IsAllowedIdentifierShape(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
        }
        return true;
    }

    private sealed class UserRowDto
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
    }
}
