using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;

using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Secrets;

namespace Servicedesk.Api.Settings;

/// Admin endpoints for managing the Microsoft Graph client secret and
/// verifying that the configured credentials work. The secret itself is
/// never returned from the server; the UI only sees whether one is stored.
public static class GraphAdminEndpoints
{
    public static IEndpointRouteBuilder MapGraphAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/settings/graph")
            .WithTags("Settings")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/secret", async (IProtectedSecretStore secrets, CancellationToken ct) =>
            Results.Ok(new { configured = await secrets.HasAsync(ProtectedSecretKeys.GraphClientSecret, ct) }))
            .WithName("GetGraphSecretStatus").WithOpenApi();

        group.MapPut("/secret", async (
            [FromBody] SetSecretRequest req,
            HttpContext http,
            IProtectedSecretStore secrets,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Value))
                return Results.BadRequest(new { error = "Client secret is required." });
            await secrets.SetAsync(ProtectedSecretKeys.GraphClientSecret, req.Value.Trim(), ct);
            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "settings.graph.secret.updated",
                Actor: actor,
                ActorRole: role,
                Target: ProtectedSecretKeys.GraphClientSecret,
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { configured = true }));
            return Results.NoContent();
        }).WithName("SetGraphSecret").WithOpenApi();

        group.MapDelete("/secret", async (
            HttpContext http,
            IProtectedSecretStore secrets,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            await secrets.DeleteAsync(ProtectedSecretKeys.GraphClientSecret, ct);
            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "settings.graph.secret.deleted",
                Actor: actor,
                ActorRole: role,
                Target: ProtectedSecretKeys.GraphClientSecret,
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { configured = false }));
            return Results.NoContent();
        }).WithName("DeleteGraphSecret").WithOpenApi();

        group.MapGet("/folders", async (
            [FromQuery] string mailbox,
            IGraphMailClient graph,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(mailbox))
                return Results.BadRequest(new { error = "Mailbox query parameter is required." });
            try
            {
                var folders = await graph.ListMailFoldersAsync(mailbox.Trim(), ct);
                return Results.Ok(folders);
            }
            catch (Exception ex)
            {
                return Results.Ok(new { error = ex.GetType().Name + ": " + ex.Message });
            }
        }).WithName("ListGraphFolders").WithOpenApi();

        group.MapPost("/test", async (
            [FromBody] TestConnectionRequest req,
            IGraphMailClient graph,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Mailbox))
                return Results.BadRequest(new { ok = false, error = "Mailbox is required." });
            try
            {
                var latency = await graph.PingAsync(req.Mailbox.Trim(), ct);
                return Results.Ok(new { ok = true, latencyMs = (int)latency.TotalMilliseconds });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.GetType().Name + ": " + ex.Message });
            }
        }).WithName("TestGraphConnection").WithOpenApi();

        return app;
    }

    public sealed record SetSecretRequest([property: Required] string Value);
    public sealed record TestConnectionRequest([property: Required] string Mailbox);
}
