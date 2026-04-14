using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Attachments;

namespace Servicedesk.Api.Settings;

/// Read-only admin view of a mail's attachment pipeline. Answers the question
/// "why is this attachment / inline image missing from the ticket?" by
/// exposing attachment-row state, ingest-job state, and blob presence.
public static class AdminMailDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapAdminMailDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/mail/diagnostics")
            .WithTags("Settings")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (
            int? limit, bool? onlyIssues,
            IMailAttachmentDiagnostics diagnostics, CancellationToken ct) =>
        {
            var rows = await diagnostics.ListRecentAsync(
                limit ?? 25, onlyIssues ?? false, ct);
            return Results.Ok(rows);
        }).WithName("ListMailAttachmentDiagnostics").WithOpenApi();

        group.MapGet("/{mailMessageId:guid}", async (
            Guid mailMessageId, HttpContext http,
            IMailAttachmentDiagnostics diagnostics, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var result = await diagnostics.GetAsync(mailMessageId, ct);
            if (result is null) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "mail.diagnostics.view",
                Actor: actor,
                ActorRole: role,
                Target: mailMessageId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { ticketId = result.TicketId, attachmentCount = result.Attachments.Count }));

            return Results.Ok(result);
        }).WithName("GetMailAttachmentDiagnostics").WithOpenApi();

        return app;
    }
}
