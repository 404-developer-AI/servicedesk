using Servicedesk.Infrastructure.Health.SecurityActivity;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Health;

/// Clears the subsystem-specific source state that drives HealthAggregator's
/// intrinsic status so that acknowledging an incident actually makes the card
/// flip back to green. Called from the incident-ack endpoints after the
/// incidents row is marked acknowledged.
public interface IHealthSubsystemReset
{
    Task<IReadOnlyList<string>> ResetAsync(string subsystem, CancellationToken ct);
}

public sealed class HealthSubsystemReset : IHealthSubsystemReset
{
    private readonly IQueueInboundMailboxRepository _sources;
    private readonly IBlobStoreHealth _blobHealth;
    private readonly ISecurityActivitySnapshot _securityActivity;

    public HealthSubsystemReset(
        IQueueInboundMailboxRepository sources,
        IBlobStoreHealth blobHealth,
        ISecurityActivitySnapshot securityActivity)
    {
        _sources = sources;
        _blobHealth = blobHealth;
        _securityActivity = securityActivity;
    }

    public async Task<IReadOnlyList<string>> ResetAsync(string subsystem, CancellationToken ct)
    {
        switch (subsystem)
        {
            case "mail-polling":
            {
                var sources = await _sources.ListAllAsync(ct);
                foreach (var s in sources)
                {
                    await _sources.ResetFailuresAsync(s.Id, ct);
                }
                return new[] { "queue_inbound_mailboxes.consecutive_failures", "queue_inbound_mailboxes.last_error", "queue_inbound_mailboxes.last_mailbox_action_error" };
            }
            case "blob-store":
                _blobHealth.Clear();
                return new[] { "blob_store.consecutive_failures" };
            case "security-activity":
                // Register the ack moment as a counter baseline so the next
                // monitor tick only counts events that arrive AFTER the
                // acknowledge. Without this, the admin would ack a Warning
                // only to see it flip red again on the next tick because
                // the already-ack'd events are still inside the window.
                // If the attack genuinely continues, post-ack events will
                // cross the threshold again and a fresh incident + toast
                // fire — admins are not silenced, just un-duplicated.
                _securityActivity.Acknowledge(DateTime.UtcNow);
                return new[] { "security_activity.snapshot", "security_activity.counter_baseline" };
            default:
                // graph-auth, attachment-jobs, tls-cert: intrinsic state
                // (missing secret, dead-letter rows, cert file on disk)
                // can't be cleared by an acknowledge alone.
                return Array.Empty<string>();
        }
    }
}
