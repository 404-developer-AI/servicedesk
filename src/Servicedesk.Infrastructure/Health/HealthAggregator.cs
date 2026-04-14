using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Health;

/// Aggregates subsystem health into a single report. Each subsystem is a
/// small pure method so adding new ones (disk, attachment-jobs, ...) is
/// just a new private helper + a new entry in <see cref="CollectAsync"/>.
public sealed class HealthAggregator : IHealthAggregator
{
    // Threshold mirrors MailPollingService's skip-after-5 behaviour so the
    // UI flips to Critical exactly when the poller stops trying.
    private const int MailPollingCriticalThreshold = 5;

    // Pending ingest-jobs older than this flip the subsystem to Warning — long
    // enough to ignore normal backlog bursts, short enough to surface a stuck
    // worker before the inbox fills up.
    private static readonly TimeSpan AttachmentBacklogWarnAge = TimeSpan.FromMinutes(5);

    // A single blob-store write failure already surfaces a Warning — these are
    // rare (disk full / misconfig / permissions) and not retried by the caller,
    // so even one deserves admin attention. Three consecutive without an
    // intervening success flips to Critical.
    private const int BlobStoreCriticalThreshold = 3;

    private readonly IMailPollStateRepository _pollState;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IProtectedSecretStore _secrets;
    private readonly IAttachmentJobRepository _attachmentJobs;
    private readonly IBlobStoreHealth _blobHealth;
    private readonly IIncidentLog _incidents;

    public HealthAggregator(
        IMailPollStateRepository pollState,
        ITaxonomyRepository taxonomy,
        IProtectedSecretStore secrets,
        IAttachmentJobRepository attachmentJobs,
        IBlobStoreHealth blobHealth,
        IIncidentLog incidents)
    {
        _pollState = pollState;
        _taxonomy = taxonomy;
        _secrets = secrets;
        _attachmentJobs = attachmentJobs;
        _blobHealth = blobHealth;
        _incidents = incidents;
    }

    public async Task<HealthReport> CollectAsync(CancellationToken ct)
    {
        var openIncidents = await _incidents.GetOpenBySubsystemAsync(ct);

        var subsystems = new List<SubsystemHealth>
        {
            ApplyIncidents(await BuildMailPollingAsync(ct), openIncidents),
            ApplyIncidents(await BuildGraphAuthAsync(ct), openIncidents),
            ApplyIncidents(await BuildAttachmentJobsAsync(ct), openIncidents),
            ApplyIncidents(BuildBlobStore(), openIncidents),
        };
        var rollup = subsystems.Aggregate(HealthStatus.Ok,
            (acc, s) => s.Status > acc ? s.Status : acc);
        return new HealthReport(rollup, subsystems);
    }

    private static SubsystemHealth ApplyIncidents(
        SubsystemHealth sub, IReadOnlyDictionary<string, IncidentSeverity> open)
    {
        if (!open.TryGetValue(sub.Key, out var sev)) return sub;

        var bumped = sev == IncidentSeverity.Critical ? HealthStatus.Critical : HealthStatus.Warning;
        if (sub.Status >= bumped) return sub;

        var details = sub.Details.ToList();
        details.Add(new HealthDetail(
            "Unacknowledged incidents",
            "One or more unacknowledged Warning/Error log events — see Incidents list below. Acknowledge to clear."));

        return sub with
        {
            Status = bumped,
            Details = details,
        };
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
            else if (!string.IsNullOrWhiteSpace(state.LastMailboxActionError))
            {
                if (status < HealthStatus.Warning) status = HealthStatus.Warning;
                summaryParts.Add($"{queue.Name}: mailbox action failing");
                var when = state.LastMailboxActionErrorUtc is { } ts
                    ? ts.ToString("u")
                    : "(unknown time)";
                details.Add(new HealthDetail(label,
                    $"Delta polling OK, but a post-ingest mailbox action failed at {when}: {state.LastMailboxActionError}. " +
                    "Check that the Graph app has Mail.ReadWrite (application) permission with admin consent."));
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

    private async Task<SubsystemHealth> BuildAttachmentJobsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var backlog = await _attachmentJobs.CountPendingOlderThanAsync(AttachmentBacklogWarnAge, now, ct);
        var deadLetters = await _attachmentJobs.CountDeadLetteredAsync(ct);

        var status = HealthStatus.Ok;
        var details = new List<HealthDetail>();
        var actions = new List<HealthAction>();

        if (deadLetters > 0)
        {
            status = HealthStatus.Critical;
            details.Add(new HealthDetail("Dead-lettered",
                $"{deadLetters} job(s) exhausted their retries — attachments won't render until requeued."));
            actions.Add(new HealthAction(
                Key: "requeue-attachment-dead-letters",
                Label: "Requeue dead-lettered jobs",
                Endpoint: "/api/admin/health/attachment-jobs/requeue-dead-lettered",
                ConfirmMessage: $"Requeue all {deadLetters} dead-lettered attachment job(s) for another try?"));
            actions.Add(new HealthAction(
                Key: "cancel-attachment-dead-letters",
                Label: "Cancel dead-lettered jobs",
                Endpoint: "/api/admin/health/attachment-jobs/cancel-dead-lettered",
                ConfirmMessage: $"Cancel all {deadLetters} dead-lettered attachment job(s)? Their attachments will be marked Failed and the health card will flip back to green. Attempt history is kept for forensics."));
        }

        if (backlog > 0)
        {
            if (status < HealthStatus.Warning) status = HealthStatus.Warning;
            details.Add(new HealthDetail("Backlog",
                $"{backlog} ingest job(s) pending for more than {(int)AttachmentBacklogWarnAge.TotalMinutes} minute(s)."));
        }

        if (status == HealthStatus.Ok)
        {
            details.Add(new HealthDetail("Queue", "No backlog, no dead letters."));
        }

        var summary = status switch
        {
            HealthStatus.Critical => $"{deadLetters} dead-lettered job(s).",
            HealthStatus.Warning => $"{backlog} job(s) stuck in Pending > {(int)AttachmentBacklogWarnAge.TotalMinutes}m.",
            _ => "Attachment pipeline healthy.",
        };

        return new SubsystemHealth(
            Key: "attachment-jobs",
            Label: "Attachment pipeline",
            Status: status,
            Summary: summary,
            Details: details,
            Actions: actions);
    }

    private SubsystemHealth BuildBlobStore()
    {
        var snap = _blobHealth.Snapshot();
        var details = new List<HealthDetail>();
        var actions = new List<HealthAction>();

        if (snap.ConsecutiveFailures == 0)
        {
            var last = snap.LastSuccessUtc is { } ts
                ? $"Last successful write {ts:u}."
                : "No writes observed yet.";
            details.Add(new HealthDetail("Writes", last));
            return new SubsystemHealth(
                Key: "blob-store",
                Label: "Blob storage",
                Status: HealthStatus.Ok,
                Summary: "Blob writes healthy.",
                Details: details,
                Actions: actions);
        }

        var status = snap.ConsecutiveFailures >= BlobStoreCriticalThreshold
            ? HealthStatus.Critical
            : HealthStatus.Warning;

        var when = snap.LastErrorUtc is { } errTs ? errTs.ToString("u") : "(unknown time)";
        details.Add(new HealthDetail(
            "Last failure",
            $"{snap.ConsecutiveFailures} consecutive failure(s). Last {snap.LastOperation ?? "write"} at {when}: {snap.LastError}"));
        details.Add(new HealthDetail(
            "Hint",
            "Check that Storage.BlobRoot is an absolute, existing path the app can write to. " +
            "Default on Linux: /var/lib/servicedesk/blobs. On Windows dev: e.g. C:\\ProgramData\\servicedesk\\blobs."));

        actions.Add(new HealthAction(
            Key: "clear-blob-store-failures",
            Label: "Clear blob-store error",
            Endpoint: "/api/admin/health/blob-store/clear",
            ConfirmMessage: "Clear the blob-store failure counter? The next write will re-evaluate health."));

        var summary = status == HealthStatus.Critical
            ? $"{snap.ConsecutiveFailures} consecutive blob write failures — uploads, raw .eml, and HTML bodies are not being persisted."
            : "Blob write failure detected — check Storage.BlobRoot configuration.";

        return new SubsystemHealth(
            Key: "blob-store",
            Label: "Blob storage",
            Status: status,
            Summary: summary,
            Details: details,
            Actions: actions);
    }
}
