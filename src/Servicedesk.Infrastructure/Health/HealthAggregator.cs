using Microsoft.Extensions.Options;
using Servicedesk.Infrastructure.Health.SecurityActivity;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
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

    // Adsolut sliding-window length set by Wolters Kluwer. Not exposed as a
    // setting because changing our perception of it would silently mask a
    // real expiry — the only user-tunable knob is RefreshWarnDays (when to
    // start warning *before* this 30-day mark).
    private static readonly TimeSpan AdsolutRefreshWindow = TimeSpan.FromDays(30);

    private readonly IMailPollStateRepository _pollState;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IProtectedSecretStore _secrets;
    private readonly IAttachmentJobRepository _attachmentJobs;
    private readonly IBlobStoreHealth _blobHealth;
    private readonly IIncidentLog _incidents;
    private readonly ITlsCertReader _tlsCert;
    private readonly ICertRenewalTrigger _certRenewal;
    private readonly IOptions<TlsCertHealthOptions> _tlsOptions;
    private readonly ISecurityActivitySnapshot _securityActivity;
    private readonly IAdsolutConnectionStore _adsolutConnections;
    private readonly ISettingsService _settings;

    public HealthAggregator(
        IMailPollStateRepository pollState,
        ITaxonomyRepository taxonomy,
        IProtectedSecretStore secrets,
        IAttachmentJobRepository attachmentJobs,
        IBlobStoreHealth blobHealth,
        IIncidentLog incidents,
        ITlsCertReader tlsCert,
        ICertRenewalTrigger certRenewal,
        IOptions<TlsCertHealthOptions> tlsOptions,
        ISecurityActivitySnapshot securityActivity,
        IAdsolutConnectionStore adsolutConnections,
        ISettingsService settings)
    {
        _pollState = pollState;
        _taxonomy = taxonomy;
        _secrets = secrets;
        _attachmentJobs = attachmentJobs;
        _blobHealth = blobHealth;
        _incidents = incidents;
        _tlsCert = tlsCert;
        _certRenewal = certRenewal;
        _tlsOptions = tlsOptions;
        _securityActivity = securityActivity;
        _adsolutConnections = adsolutConnections;
        _settings = settings;
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
            ApplyIncidents(BuildTlsCert(), openIncidents),
            ApplyIncidents(BuildSecurityActivity(), openIncidents),
        };

        // Optional subsystems: append only when relevant. Keeps the Health
        // page from growing a "monitoring disabled" row for every external
        // integration that hasn't been wired up yet.
        var adsolut = await BuildAdsolutAsync(ct);
        if (adsolut is not null) subsystems.Add(ApplyIncidents(adsolut, openIncidents));

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

    /// Adsolut OAuth integration. Returns <c>null</c> when the integration
    /// has not been configured (no client_id + secret) so the Health page
    /// stays clean for installs that don't use it. Once configured the card
    /// always shows: Ok when connected and the sliding-month window is
    /// healthy, Warning when the window is approaching expiry or a recent
    /// refresh failed, Critical when the refresh token has been revoked
    /// (invalid_grant) or the window has actually elapsed.
    private async Task<SubsystemHealth?> BuildAdsolutAsync(CancellationToken ct)
    {
        var clientId = (await _settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        var hasClientSecret = await _secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        if (string.IsNullOrEmpty(clientId) || !hasClientSecret)
        {
            return null;
        }

        var hasRefreshToken = await _secrets.HasAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);
        var connection = await _adsolutConnections.GetAsync(ct);
        var warnDays = await _settings.GetAsync<int>(SettingKeys.Adsolut.RefreshWarnDays, ct);
        if (warnDays <= 0) warnDays = 7;

        var details = new List<HealthDetail>();
        var actions = new List<HealthAction>();
        HealthStatus status;
        string summary;

        if (!hasRefreshToken)
        {
            status = HealthStatus.Warning;
            summary = "Configured but not connected — admin still needs to authorize via Settings → Integrations.";
            details.Add(new HealthDetail("State", "Configured, awaiting first authorization."));
            return new SubsystemHealth(
                Key: "adsolut-integration",
                Label: "Adsolut integration",
                Status: status,
                Summary: summary,
                Details: details,
                Actions: actions);
        }

        // From here on we have a refresh token. Compute sliding-window
        // remaining vs. the configurable warn threshold.
        DateTime? slidingExpiry = connection?.LastRefreshedUtc + AdsolutRefreshWindow;
        double? daysLeft = slidingExpiry is null
            ? null
            : (slidingExpiry.Value - DateTime.UtcNow).TotalDays;
        int? daysLeftRounded = daysLeft is null ? null : (int)Math.Floor(daysLeft.Value);

        var lastError = connection?.LastRefreshError;
        var lastErrorIsGrant = string.Equals(lastError, "invalid_grant", StringComparison.Ordinal);

        if (lastErrorIsGrant)
        {
            status = HealthStatus.Critical;
            summary = "Refresh token revoked — admin must reconnect via Settings → Integrations.";
        }
        else if (daysLeft is not null && daysLeft.Value <= 0)
        {
            status = HealthStatus.Critical;
            summary = "Refresh window has expired — admin must reconnect.";
        }
        else if (daysLeft is not null && daysLeft.Value <= warnDays)
        {
            status = HealthStatus.Warning;
            summary = $"Refresh window expires in {daysLeftRounded} day(s) — run Test refresh or reconnect soon.";
        }
        else if (!string.IsNullOrEmpty(lastError))
        {
            status = HealthStatus.Warning;
            summary = "A recent refresh failed; the connection still has a valid token but should be retested.";
        }
        else
        {
            status = HealthStatus.Ok;
            summary = daysLeftRounded is not null
                ? $"Connected, refresh window has {daysLeftRounded} day(s) left."
                : "Connected.";
        }

        details.Add(new HealthDetail(
            "Authorized as",
            connection?.AuthorizedEmail ?? connection?.AuthorizedSubject ?? "(unknown subject)"));
        if (connection?.AuthorizedUtc is { } authorized)
        {
            details.Add(new HealthDetail("Authorized at", authorized.ToString("u")));
        }
        if (connection?.LastRefreshedUtc is { } lastRefreshed)
        {
            details.Add(new HealthDetail("Last refreshed", lastRefreshed.ToString("u")));
        }
        if (slidingExpiry is { } exp)
        {
            details.Add(new HealthDetail(
                "Refresh window expires",
                $"{exp:u} ({(daysLeftRounded is null ? "?" : daysLeftRounded.ToString())} day(s))"));
        }
        if (!string.IsNullOrEmpty(lastError))
        {
            var when = connection?.LastRefreshErrorUtc is { } errTs ? errTs.ToString("u") : "(unknown time)";
            details.Add(new HealthDetail("Last refresh error", $"{lastError} at {when}"));
        }

        // Action button mirrors the per-page "Test refresh" — a single
        // POST that exercises the whole token-rotation path. Skipped when
        // we already know the RT is revoked: clicking it would just
        // produce another invalid_grant.
        if (!lastErrorIsGrant)
        {
            actions.Add(new HealthAction(
                Key: "test-adsolut-refresh",
                Label: "Test refresh",
                Endpoint: "/api/admin/integrations/adsolut/refresh",
                ConfirmMessage: null));
        }

        return new SubsystemHealth(
            Key: "adsolut-integration",
            Label: "Adsolut integration",
            Status: status,
            Summary: summary,
            Details: details,
            Actions: actions);
    }
}
