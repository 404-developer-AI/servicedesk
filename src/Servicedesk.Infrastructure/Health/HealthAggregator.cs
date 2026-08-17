using Microsoft.Extensions.Options;
using Servicedesk.Infrastructure.Health.SecurityActivity;
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

    private readonly IQueueInboundMailboxRepository _sources;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IProtectedSecretStore _secrets;
    private readonly IAttachmentJobRepository _attachmentJobs;
    private readonly IBlobStoreHealth _blobHealth;
    private readonly IIncidentLog _incidents;
    private readonly ITlsCertReader _tlsCert;
    private readonly ICertRenewalTrigger _certRenewal;
    private readonly IOptions<TlsCertHealthOptions> _tlsOptions;
    private readonly ISecurityActivitySnapshot _securityActivity;
    private readonly Retention.IRetentionHealth _retention;

    public HealthAggregator(
        IQueueInboundMailboxRepository sources,
        ITaxonomyRepository taxonomy,
        IProtectedSecretStore secrets,
        IAttachmentJobRepository attachmentJobs,
        IBlobStoreHealth blobHealth,
        IIncidentLog incidents,
        ITlsCertReader tlsCert,
        ICertRenewalTrigger certRenewal,
        IOptions<TlsCertHealthOptions> tlsOptions,
        ISecurityActivitySnapshot securityActivity,
        Retention.IRetentionHealth retention)
    {
        _retention = retention;
        _sources = sources;
        _taxonomy = taxonomy;
        _secrets = secrets;
        _attachmentJobs = attachmentJobs;
        _blobHealth = blobHealth;
        _incidents = incidents;
        _tlsCert = tlsCert;
        _certRenewal = certRenewal;
        _tlsOptions = tlsOptions;
        _securityActivity = securityActivity;
    }

    public async Task<HealthReport> CollectAsync(CancellationToken ct)
    {
        var openIncidents = await _incidents.GetOpenBySubsystemAsync(ct);

        // v0.0.27: Adsolut moved out of System health into its own
        // IntegrationsHealthAggregator + dashboard tile. The roll-up below
        // covers core platform subsystems only; the dashboard pill merges
        // both aggregators.
        var subsystems = new List<SubsystemHealth>
        {
            ApplyIncidents(await BuildMailPollingAsync(ct), openIncidents),
            ApplyIncidents(await BuildGraphAuthAsync(ct), openIncidents),
            ApplyIncidents(await BuildAttachmentJobsAsync(ct), openIncidents),
            ApplyIncidents(BuildBlobStore(), openIncidents),
            ApplyIncidents(BuildTlsCert(), openIncidents),
            ApplyIncidents(BuildSecurityActivity(), openIncidents),
            ApplyIncidents(BuildDataRetention(), openIncidents),
        };

        var rollup = subsystems.Aggregate(HealthStatus.Ok,
            (acc, s) => s.Status > acc ? s.Status : acc);
        return new HealthReport(rollup, subsystems);
    }

    /// v0.0.101 — the generic housekeeping sweep (RetentionWorker). Warning
    /// when the last sweep threw; otherwise informational (last run, rows
    /// pruned per table, next run). Settings live under Settings → Health →
    /// Data retention.
    private SubsystemHealth BuildDataRetention()
    {
        var snap = _retention.Snapshot();
        var details = new List<HealthDetail>();

        if (snap.LastRunUtc is { } last)
        {
            var pruned = snap.LastDeletedPerTable.Count == 0
                ? "nothing to prune"
                : string.Join(", ", snap.LastDeletedPerTable.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}: {kv.Value}"));
            if (string.IsNullOrEmpty(pruned)) pruned = "nothing to prune";
            var secs = (snap.LastDuration?.TotalSeconds ?? 0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            details.Add(new HealthDetail("Last sweep", $"{last:u} ({secs} s) — {pruned}."));
            details.Add(new HealthDetail("Rows pruned since start", snap.TotalDeletedSinceStart.ToString()));
        }
        else
        {
            details.Add(new HealthDetail("Last sweep", "Not run yet (first sweep 2 minutes after start)."));
        }
        if (snap.NextRunUtc is { } next)
            details.Add(new HealthDetail("Next sweep", next.ToString("u")));

        if (snap.LastError is not null)
        {
            details.Add(new HealthDetail("Last error", $"{snap.LastErrorUtc:u}: {snap.LastError}"));
            return new SubsystemHealth(
                Key: "data-retention",
                Label: "Data retention",
                Status: HealthStatus.Warning,
                Summary: "The last retention sweep failed — housekeeping tables keep growing until it succeeds.",
                Details: details,
                Actions: Array.Empty<HealthAction>());
        }

        return new SubsystemHealth(
            Key: "data-retention",
            Label: "Data retention",
            Status: HealthStatus.Ok,
            Summary: "Housekeeping sweep healthy.",
            Details: details,
            Actions: Array.Empty<HealthAction>());
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
        var queues = (await _taxonomy.ListQueuesAsync(ct)).ToDictionary(q => q.Id);
        var sources = await _sources.ListAllAsync(ct);
        // Only sources whose queue exists and is active are eligible to poll.
        var configured = sources
            .Where(s => queues.TryGetValue(s.QueueId, out var q) && q.IsActive)
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

        var status = HealthStatus.Ok;
        var details = new List<HealthDetail>();
        var actions = new List<HealthAction>();
        var summaryParts = new List<string>();

        foreach (var src in configured)
        {
            var queueName = queues.TryGetValue(src.QueueId, out var q) ? q.Name : src.QueueId.ToString();
            var folderSuffix = string.IsNullOrWhiteSpace(src.FolderName) ? "" : $" / {src.FolderName}";
            var label = $"{queueName} ({src.MailboxAddress}{folderSuffix})";

            if (string.IsNullOrWhiteSpace(src.FolderId))
            {
                details.Add(new HealthDetail(label, "No inbound folder selected yet — not polling."));
            }
            else if (src.ConsecutiveFailures >= MailPollingCriticalThreshold)
            {
                status = HealthStatus.Critical;
                summaryParts.Add($"{queueName}: paused after {src.ConsecutiveFailures} failures");
                details.Add(new HealthDetail(label,
                    $"PAUSED — {src.ConsecutiveFailures} consecutive failures. Last error: {src.LastError ?? "(none)"}"));
                actions.Add(new HealthAction(
                    Key: $"reset-{src.Id}",
                    Label: $"Reset {queueName} failures",
                    Endpoint: $"/api/admin/health/mail-polling/sources/{src.Id}/reset",
                    ConfirmMessage: $"Clear the failure counter for {queueName} ({src.MailboxAddress})? The next polling cycle will retry the mailbox."));
            }
            else if (src.ConsecutiveFailures > 0)
            {
                if (status < HealthStatus.Warning) status = HealthStatus.Warning;
                summaryParts.Add($"{queueName}: {src.ConsecutiveFailures} recent failure(s)");
                details.Add(new HealthDetail(label,
                    $"{src.ConsecutiveFailures} recent failure(s). Last error: {src.LastError ?? "(none)"}"));
            }
            else if (!string.IsNullOrWhiteSpace(src.LastMailboxActionError))
            {
                if (status < HealthStatus.Warning) status = HealthStatus.Warning;
                summaryParts.Add($"{queueName}: mailbox action failing");
                var when = src.LastMailboxActionErrorUtc is { } ts
                    ? ts.ToString("u")
                    : "(unknown time)";
                details.Add(new HealthDetail(label,
                    $"Delta polling OK, but a post-ingest mailbox action failed at {when}: {src.LastMailboxActionError}. " +
                    "Check that the Graph app has Mail.ReadWrite (application) permission with admin consent."));
            }
            else if (!src.PollingEnabled)
            {
                details.Add(new HealthDetail(label, "Polling paused by an admin."));
            }
            else
            {
                var last = src.LastPolledUtc is { } ts
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

    private SubsystemHealth BuildTlsCert()
    {
        var opts = _tlsOptions.Value;
        var details = new List<HealthDetail>();
        var actions = new List<HealthAction>();

        if (string.IsNullOrWhiteSpace(opts.Domain))
        {
            // SSL=no install, or a pre-v0.0.18 upgrade that has not yet
            // re-run update.sh with the TlsCert backfill. No cert file to
            // read — report Ok with an explanatory line rather than a
            // spurious warning.
            return new SubsystemHealth(
                Key: "tls-cert",
                Label: "TLS certificate",
                Status: HealthStatus.Ok,
                Summary: "TLS monitoring disabled — no domain configured.",
                Details: new[]
                {
                    new HealthDetail("Domain",
                        "Not configured. Run install.sh with SSL=yes, or set SERVICEDESK_TlsCert__Domain in /etc/servicedesk/env.conf."),
                },
                Actions: Array.Empty<HealthAction>());
        }

        var info = _tlsCert.Read();
        var status = HealthStatus.Ok;
        string summary;

        AppendLastRun(details);

        if (info is null)
        {
            // Domain is set but the cert file is missing — typically the
            // one short window between install.sh running and certbot's
            // first-issue finishing, OR a broken state where the certbot
            // volume lost its content. Either way: Warning, let admin
            // trigger renewal.
            status = HealthStatus.Warning;
            summary = $"No certificate found for {opts.Domain}.";
            details.Add(new HealthDetail("Domain", opts.Domain));
            details.Add(new HealthDetail("Certificate",
                $"Expected at {opts.CertDirectory}/{opts.Domain}/fullchain.pem — not readable."));
            actions.Add(BuildRenewAction(opts.Domain));
            return new SubsystemHealth(
                Key: "tls-cert",
                Label: "TLS certificate",
                Status: status,
                Summary: summary,
                Details: details,
                Actions: actions);
        }

        var daysLeft = (info.NotAfterUtc - DateTime.UtcNow).TotalDays;
        var daysLeftRounded = (int)Math.Floor(daysLeft);

        if (daysLeft < 0)
        {
            status = HealthStatus.Critical;
            summary = $"Certificate expired {Math.Abs(daysLeftRounded)} day(s) ago — nginx is serving an invalid cert.";
        }
        else if (daysLeft < opts.CriticalDays)
        {
            status = HealthStatus.Critical;
            summary = $"Certificate expires in {daysLeftRounded} day(s) — renew immediately.";
        }
        else if (daysLeft < opts.WarningDays)
        {
            status = HealthStatus.Warning;
            summary = $"Certificate expires in {daysLeftRounded} day(s).";
        }
        else
        {
            summary = $"Certificate valid for {daysLeftRounded} more day(s).";
        }

        details.Add(new HealthDetail("Domain", opts.Domain));
        details.Add(new HealthDetail("Subject", info.Subject));
        details.Add(new HealthDetail("Expires", info.NotAfterUtc.ToString("u")));
        details.Add(new HealthDetail("Days remaining", daysLeftRounded.ToString()));

        actions.Add(BuildRenewAction(opts.Domain));

        return new SubsystemHealth(
            Key: "tls-cert",
            Label: "TLS certificate",
            Status: status,
            Summary: summary,
            Details: details,
            Actions: actions);
    }

    private void AppendLastRun(List<HealthDetail> details)
    {
        var status = _certRenewal.TryReadStatus();
        if (status is null) return;

        var label = status.State switch
        {
            "running" => "Last renew attempt",
            "success" => "Last renew attempt",
            "failed" => "Last renew attempt",
            _ => "Last renew attempt",
        };
        var value = status.Detail is null
            ? $"{status.State} at {status.WhenUtc:u}"
            : $"{status.State} at {status.WhenUtc:u} — {status.Detail}";
        details.Add(new HealthDetail(label, value));
    }

    private static HealthAction BuildRenewAction(string domain) => new(
        Key: "renew-tls-cert",
        Label: "Renew now",
        Endpoint: "/api/admin/health/tls-cert/renew",
        ConfirmMessage:
            $"Request a Let's Encrypt renewal for {domain}? " +
            "Certbot runs on the host (webroot challenge via nginx) and nginx is " +
            "reloaded automatically on success. Watch this card for the result.");

    private SubsystemHealth BuildSecurityActivity()
    {
        var snap = _securityActivity.Get();
        if (snap is null)
        {
            return new SubsystemHealth(
                Key: "security-activity",
                Label: "Security activity",
                Status: HealthStatus.Ok,
                Summary: "Waiting for first evaluation cycle…",
                Details: Array.Empty<HealthDetail>(),
                Actions: Array.Empty<HealthAction>());
        }

        var details = new List<HealthDetail>();
        if (!snap.MonitorEnabled)
        {
            details.Add(new HealthDetail(
                "Status",
                "Disabled — toggle Health.SecurityActivity.Enabled to start sampling."));
        }
        else
        {
            details.Add(new HealthDetail(
                "Window",
                $"{(int)snap.Window.TotalSeconds}s rolling, evaluated {snap.EvaluatedUtc:u}"));

            if (snap.AcknowledgedFromUtc is { } ack)
            {
                details.Add(new HealthDetail(
                    "Counter reset",
                    $"Acknowledged at {ack:u} — only counting events after that moment until the window has fully rolled past."));
            }

            foreach (var c in snap.Categories)
            {
                var lvl = c.Status switch
                {
                    HealthStatus.Critical => $"CRITICAL ({c.Count} ≥ {c.CriticalThreshold})",
                    HealthStatus.Warning => $"WARNING ({c.Count} ≥ {c.Threshold})",
                    _ => $"{c.Count} / {c.Threshold}",
                };
                details.Add(new HealthDetail(c.Label, lvl));
            }
        }

        return new SubsystemHealth(
            Key: "security-activity",
            Label: "Security activity",
            Status: snap.Status,
            Summary: snap.Summary,
            Details: details,
            Actions: Array.Empty<HealthAction>());
    }

}
