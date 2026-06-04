using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Api.Settings;

/// Admin view + control of inbound mail polling, one row per queue that has an
/// inbound mailbox configured. The toggle flips `inbound_polling_enabled` on
/// the queue; the MailPollingService skips paused mailboxes on its next cycle
/// while leaving their delta-state intact, so resuming picks up where it left
/// off. Surfaced under Settings → Mail.
public static class MailMailboxEndpoints
{
    public static IEndpointRouteBuilder MapMailMailboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/mail/mailboxes")
            .WithTags("Settings")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (
            ITaxonomyRepository taxonomy, IMailPollStateRepository stateRepo,
            CancellationToken ct) =>
        {
            var queues = await taxonomy.ListQueuesAsync(ct);
            var states = (await stateRepo.ListAllAsync(ct)).ToDictionary(s => s.QueueId);

            var mailboxes = queues
                .Where(q => !string.IsNullOrWhiteSpace(q.InboundMailboxAddress))
                .OrderBy(q => q.SortOrder).ThenBy(q => q.Name)
                .Select(q =>
                {
                    states.TryGetValue(q.Id, out var st);
                    return new
                    {
                        queueId = q.Id,
                        queueName = q.Name,
                        mailbox = q.InboundMailboxAddress,
                        folderName = q.InboundFolderName,
                        folderConfigured = !string.IsNullOrWhiteSpace(q.InboundFolderId),
                        isActive = q.IsActive,
                        inboundPollingEnabled = q.InboundPollingEnabled,
                        lastPolledUtc = st?.LastPolledUtc,
                        lastError = st?.LastError,
                        consecutiveFailures = st?.ConsecutiveFailures ?? 0,
                    };
                });

            return Results.Ok(mailboxes);
        }).WithName("ListInboundMailboxes").WithOpenApi();

        group.MapPut("/{queueId:guid}/polling", async (
            Guid queueId, PollingToggleRequest req, HttpContext http,
            ITaxonomyRepository taxonomy, IAuditLogger audit, CancellationToken ct) =>
        {
            var updated = await taxonomy.SetQueueInboundPollingAsync(queueId, req.Enabled, ct);
            if (!updated) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: req.Enabled ? "mail.polling.enable" : "mail.polling.disable",
                Actor: actor,
                ActorRole: role,
                Target: queueId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { enabled = req.Enabled }));

            return Results.Ok(new { queueId, inboundPollingEnabled = req.Enabled });
        }).WithName("SetInboundMailboxPolling").WithOpenApi();

        return app;
    }

    public sealed record PollingToggleRequest(bool Enabled);
}
