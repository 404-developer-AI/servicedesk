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
    private readonly IMailPollStateRepository _pollState;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IBlobStoreHealth _blobHealth;

    public HealthSubsystemReset(
        IMailPollStateRepository pollState,
        ITaxonomyRepository taxonomy,
        IBlobStoreHealth blobHealth)
    {
        _pollState = pollState;
        _taxonomy = taxonomy;
        _blobHealth = blobHealth;
    }

    public async Task<IReadOnlyList<string>> ResetAsync(string subsystem, CancellationToken ct)
    {
        switch (subsystem)
        {
            case "mail-polling":
            {
                var queues = await _taxonomy.ListQueuesAsync(ct);
                foreach (var q in queues)
                {
                    await _pollState.ResetFailuresAsync(q.Id, ct);
                }
                return new[] { "mail_poll_state.consecutive_failures", "mail_poll_state.last_error", "mail_poll_state.last_mailbox_action_error" };
            }
            case "blob-store":
                _blobHealth.Clear();
                return new[] { "blob_store.consecutive_failures" };
            default:
                // graph-auth, attachment-jobs: intrinsic state (missing secret,
                // dead-letter rows) can't be cleared by an acknowledge alone.
                return Array.Empty<string>();
        }
    }
}
