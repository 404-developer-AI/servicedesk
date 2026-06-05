using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Api.Settings;

/// Admin view + control of inbound mail polling, one row per inbound-mailbox
/// source (v0.0.66 — a queue can have several). The toggle flips
/// `polling_enabled` on the source; the MailPollingService skips paused sources
/// on its next cycle while leaving their delta-state intact, so resuming picks
/// up where it left off. Surfaced under Settings → Mail.
public static class MailMailboxEndpoints
{
    public static IEndpointRouteBuilder MapMailMailboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/mail/mailboxes")
            .WithTags("Settings")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (
            ITaxonomyRepository taxonomy, IQueueInboundMailboxRepository sources,
            CancellationToken ct) =>
        {
            var queues = (await taxonomy.ListQueuesAsync(ct)).ToDictionary(q => q.Id);

            var mailboxes = (await sources.ListAllAsync(ct))
                .Select(s =>
                {
                    queues.TryGetValue(s.QueueId, out var q);
                    return new
                    {
                        sourceId = s.Id,
                        queueId = s.QueueId,
                        queueName = q?.Name ?? s.QueueId.ToString(),
                        mailbox = s.MailboxAddress,
                        folderName = s.FolderName,
                        folderConfigured = !string.IsNullOrWhiteSpace(s.FolderId),
                        isActive = q?.IsActive ?? false,
                        pollingEnabled = s.PollingEnabled,
                        lastPolledUtc = s.LastPolledUtc,
                        lastError = s.LastError,
                        consecutiveFailures = s.ConsecutiveFailures,
                        sortOrder = q?.SortOrder ?? 0,
                    };
                })
                .OrderBy(x => x.sortOrder).ThenBy(x => x.queueName).ThenBy(x => x.mailbox)
                .ToList();

            return Results.Ok(mailboxes);
        }).WithName("ListInboundMailboxes").WithOpenApi();

        group.MapPut("/{sourceId:guid}/polling", async (
            Guid sourceId, PollingToggleRequest req, HttpContext http,
            IQueueInboundMailboxRepository sources, IAuditLogger audit, CancellationToken ct) =>
        {
            var updated = await sources.SetPollingAsync(sourceId, req.Enabled, ct);
            if (!updated) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: req.Enabled ? "mail.polling.enable" : "mail.polling.disable",
                Actor: actor,
                ActorRole: role,
                Target: sourceId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { enabled = req.Enabled }));

            return Results.Ok(new { sourceId, pollingEnabled = req.Enabled });
        }).WithName("SetInboundMailboxPolling").WithOpenApi();

        return app;
    }

    public sealed record PollingToggleRequest(bool Enabled);
}
