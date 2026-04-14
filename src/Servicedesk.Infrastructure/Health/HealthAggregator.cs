using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Secrets;

namespace Servicedesk.Infrastructure.Health;

/// Aggregates subsystem health into a single report. Each subsystem is a
/// small pure method so adding new ones (disk, attachment-jobs, ...) is
/// just a new private helper + a new entry in <see cref="CollectAsync"/>.
public sealed class HealthAggregator : IHealthAggregator
{
    // Threshold mirrors MailPollingService's skip-after-5 behaviour so the
    // UI flips to Critical exactly when the poller stops trying.
    private const int MailPollingCriticalThreshold = 5;

    private readonly IMailPollStateRepository _pollState;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IProtectedSecretStore _secrets;

    public HealthAggregator(
        IMailPollStateRepository pollState,
        ITaxonomyRepository taxonomy,
        IProtectedSecretStore secrets)
    {
        _pollState = pollState;
        _taxonomy = taxonomy;
        _secrets = secrets;
    }

    public async Task<HealthReport> CollectAsync(CancellationToken ct)
    {
        var subsystems = new List<SubsystemHealth>
        {
            await BuildMailPollingAsync(ct),
            await BuildGraphAuthAsync(ct),
        };
        var rollup = subsystems.Aggregate(HealthStatus.Ok,
            (acc, s) => s.Status > acc ? s.Status : acc);
        return new HealthReport(rollup, subsystems);
    }

    private async Task<SubsystemHealth> BuildMailPollingAsync(CancellationToken ct)
    {
        var queues = await _taxonomy.ListQueuesAsync(ct);
        var configured = queues
            .Where(q => q.IsActive && !string.IsNullOrWhiteSpace(q.InboundMailboxAddress))
            .ToList();

        if (configured.Count == 0)
        {
            return new SubsystemHealth(
                Key: "mail-polling",
                Label: "Mail polling",
                Status: HealthStatus.Ok,
                Summary: "No queues have an inbound mailbox configured — nothing to poll.",
                Details: Array.Empty<HealthDetail>(),
                Actions: Array.Empty<HealthAction>());
        }

        var states = await _pollState.ListAllAsync(ct);
        var stateByQueue = states.ToDictionary(s => s.QueueId);

        var status = HealthStatus.Ok;
        var details = new List<HealthDetail>();
        var actions = new List<HealthAction>();
        var summaryParts = new List<string>();

        foreach (var queue in configured)
        {
            stateByQueue.TryGetValue(queue.Id, out var state);
            var label = $"{queue.Name} ({queue.InboundMailboxAddress})";

            if (state is null)
            {
                details.Add(new HealthDetail(label, "Waiting for first poll cycle…"));
                continue;
            }

            if (state.ConsecutiveFailures >= MailPollingCriticalThreshold)
            {
                status = HealthStatus.Critical;
                summaryParts.Add($"{queue.Name}: paused after {state.ConsecutiveFailures} failures");
                details.Add(new HealthDetail(label,
                    $"PAUSED — {state.ConsecutiveFailures} consecutive failures. Last error: {state.LastError ?? "(none)"}"));
                actions.Add(new HealthAction(
                    Key: $"reset-{queue.Id}",
                    Label: $"Reset {queue.Name} failures",
                    Endpoint: $"/api/admin/health/mail-polling/queues/{queue.Id}/reset",
                    ConfirmMessage: $"Clear the failure counter for {queue.Name}? The next polling cycle will retry the mailbox."));
            }
            else if (state.ConsecutiveFailures > 0)
            {
                if (status < HealthStatus.Warning) status = HealthStatus.Warning;
                summaryParts.Add($"{queue.Name}: {state.ConsecutiveFailures} recent failure(s)");
                details.Add(new HealthDetail(label,
                    $"{state.ConsecutiveFailures} recent failure(s). Last error: {state.LastError ?? "(none)"}"));
            }
            else
            {
                var last = state.LastPolledUtc is { } ts
                    ? $"last polled {ts:u}"
                    : "not yet polled";
                details.Add(new HealthDetail(label, $"OK — {last}"));
            }
        }

        var summary = status == HealthStatus.Ok
            ? $"{configured.Count} mailbox(es) polling normally."
            : string.Join("; ", summaryParts);

        return new SubsystemHealth(
            Key: "mail-polling",
            Label: "Mail polling",
            Status: status,
            Summary: summary,
            Details: details,
            Actions: actions);
    }

    private async Task<SubsystemHealth> BuildGraphAuthAsync(CancellationToken ct)
    {
        var hasSecret = await _secrets.HasAsync(ProtectedSecretKeys.GraphClientSecret, ct);
        if (hasSecret)
        {
            return new SubsystemHealth(
                Key: "graph-auth",
                Label: "Microsoft Graph credentials",
                Status: HealthStatus.Ok,
                Summary: "Client secret is configured. Token errors surface under Mail polling.",
                Details: new[] { new HealthDetail("Client secret", "Stored (encrypted)") },
                Actions: Array.Empty<HealthAction>());
        }

        return new SubsystemHealth(
            Key: "graph-auth",
            Label: "Microsoft Graph credentials",
            Status: HealthStatus.Warning,
            Summary: "No client secret configured — mail polling cannot authenticate.",
            Details: new[] { new HealthDetail("Client secret", "Not configured") },
            Actions: Array.Empty<HealthAction>());
    }
}
