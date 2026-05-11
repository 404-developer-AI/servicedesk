using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Integrations.Telavox;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Health;

/// One integration on the dashboard tile. Each integration carries its own
/// rollup plus a fixed set of checks (today: Connection + Sync) and a list
/// of tile-level actions (today: Sync now). Per-check actions live on
/// <see cref="SubsystemHealth.Actions"/>; tile-level actions are
/// integration-wide affordances the UI renders centred next to the row.
/// The frontend uses <see cref="LogoKey"/> to pick the right brand asset —
/// the backend never deals in URLs.
public sealed record IntegrationHealth(
    string Key,
    string Name,
    string LogoKey,
    HealthStatus Rollup,
    IReadOnlyList<SubsystemHealth> Checks,
    IReadOnlyList<HealthAction> Actions);

/// Roll-up of every configured integration. The list is empty on installs
/// that have no integration configured at all — the UI hides the tile in
/// that case so a vanilla deployment doesn't show an empty card.
public sealed record IntegrationsHealthReport(
    HealthStatus Rollup,
    IReadOnlyList<IntegrationHealth> Integrations);

public interface IIntegrationsHealthAggregator
{
    Task<IntegrationsHealthReport> CollectAsync(CancellationToken ct);
}

/// v0.0.27 — split out of <see cref="HealthAggregator"/> so integration
/// health gets its own dashboard tile instead of crowding the System
/// health card. Each integration produces two checks:
/// <list type="bullet">
/// <item><b>Connection</b> — OAuth refresh-token health + sliding window
/// (mirrors what <c>BuildAdsolutAsync</c> used to do under System health).</item>
/// <item><b>Sync</b> — last sync-tick outcome + cursor staleness, with an
/// admin Acknowledge button that suppresses the current signal until the
/// next failed tick.</item>
/// </list>
/// An integration only appears once it is "configured" (clientId + secret
/// present); the connect-state of OAuth itself is then represented as a
/// Warning row inside the Connection check.
public sealed class IntegrationsHealthAggregator : IIntegrationsHealthAggregator
{
    private static readonly TimeSpan AdsolutRefreshWindow = TimeSpan.FromDays(30);
    private const int DefaultStaleIntervalMultiplier = 4;

    private readonly IProtectedSecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly IAdsolutConnectionStore _adsolutConnections;
    private readonly IAdsolutSyncStateStore _adsolutSyncState;
    private readonly ITelavoxAgentLinkStore _telavoxLinks;

    public IntegrationsHealthAggregator(
        IProtectedSecretStore secrets,
        ISettingsService settings,
        IAdsolutConnectionStore adsolutConnections,
        IAdsolutSyncStateStore adsolutSyncState,
        ITelavoxAgentLinkStore telavoxLinks)
    {
        _secrets = secrets;
        _settings = settings;
        _adsolutConnections = adsolutConnections;
        _adsolutSyncState = adsolutSyncState;
        _telavoxLinks = telavoxLinks;
    }

    public async Task<IntegrationsHealthReport> CollectAsync(CancellationToken ct)
    {
        var integrations = new List<IntegrationHealth>();

        var adsolut = await BuildAdsolutAsync(ct);
        if (adsolut is not null) integrations.Add(adsolut);

        var telavox = await BuildTelavoxAsync(ct);
        if (telavox is not null) integrations.Add(telavox);

        var rollup = integrations.Aggregate(HealthStatus.Ok,
            (acc, i) => i.Rollup > acc ? i.Rollup : acc);
        return new IntegrationsHealthReport(rollup, integrations);
    }

    // ---- Adsolut --------------------------------------------------------

    private async Task<IntegrationHealth?> BuildAdsolutAsync(CancellationToken ct)
    {
        var clientId = (await _settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        var hasClientSecret = await _secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        if (string.IsNullOrEmpty(clientId) || !hasClientSecret)
        {
            return null;
        }

        var hasRefreshToken = await _secrets.HasAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);
        var connection = await _adsolutConnections.GetAsync(ct);
        var syncState = await _adsolutSyncState.GetAsync(ct);

        var connectionCheck = BuildAdsolutConnectionCheck(hasRefreshToken, connection,
            warnDays: await ResolveWarnDaysAsync(ct));
        var syncCheck = BuildAdsolutSyncCheck(hasRefreshToken, connection, syncState,
            intervalMinutes: await ResolveSyncIntervalAsync(ct));

        var checks = new[] { connectionCheck, syncCheck };
        var rollup = checks.Aggregate(HealthStatus.Ok, (acc, c) => c.Status > acc ? c.Status : acc);

        // Tile-level actions: only "Sync now" today, gated on the same
        // pre-conditions the worker checks before doing real work — RT
        // present + a dossier picked. Showing the button when the worker
        // would just no-op would leave the admin staring at a toast that
        // never resulted in a tick.
        var tileActions = new List<HealthAction>();
        if (hasRefreshToken && connection?.AdministrationId is not null)
        {
            tileActions.Add(new HealthAction(
                Key: "adsolut-sync-now",
                Label: "Sync now",
                Endpoint: "/api/admin/integrations/adsolut/sync",
                ConfirmMessage: null));
        }

        return new IntegrationHealth(
            Key: "adsolut",
            Name: "Adsolut",
            LogoKey: "adsolut",
            Rollup: rollup,
            Checks: checks,
            Actions: tileActions);
    }

    private async Task<int> ResolveWarnDaysAsync(CancellationToken ct)
    {
        var v = await _settings.GetAsync<int>(SettingKeys.Adsolut.RefreshWarnDays, ct);
        return v <= 0 ? 7 : v;
    }

    private async Task<int> ResolveSyncIntervalAsync(CancellationToken ct)
    {
        var v = await _settings.GetAsync<int>(SettingKeys.Adsolut.SyncIntervalMinutes, ct);
        return v <= 0 ? 5 : v;
    }

    private static SubsystemHealth BuildAdsolutConnectionCheck(
        bool hasRefreshToken,
        AdsolutConnection? connection,
        int warnDays)
    {
        var details = new List<HealthDetail>();
        var actions = new List<HealthAction>();

        if (!hasRefreshToken)
        {
            details.Add(new HealthDetail("State", "Configured, awaiting first authorization."));
            return new SubsystemHealth(
                Key: "adsolut-connection",
                Label: "Connection",
                Status: HealthStatus.Warning,
                Summary: "Configured but not connected — admin still needs to authorize via Settings → Integrations.",
                Details: details,
                Actions: actions);
        }

        DateTime? slidingExpiry = connection?.LastRefreshedUtc + AdsolutRefreshWindow;
        double? daysLeft = slidingExpiry is null
            ? null
            : (slidingExpiry.Value - DateTime.UtcNow).TotalDays;
        int? daysLeftRounded = daysLeft is null ? null : (int)Math.Floor(daysLeft.Value);

        var lastError = connection?.LastRefreshError;
        var lastErrorIsGrant = string.Equals(lastError, "invalid_grant", StringComparison.Ordinal);

        HealthStatus status;
        string summary;

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

        // No actions on the Connection check — Test refresh lives on the
        // dedicated /settings/integrations/adsolut page where it sits next
        // to the Reconnect / Disconnect buttons. Putting it on the dashboard
        // tile too would just duplicate the path without adding value.

        return new SubsystemHealth(
            Key: "adsolut-connection",
            Label: "Connection",
            Status: status,
            Summary: summary,
            Details: details,
            Actions: actions);
    }

    private static SubsystemHealth BuildAdsolutSyncCheck(
        bool hasRefreshToken,
        AdsolutConnection? connection,
        AdsolutSyncState? syncState,
        int intervalMinutes)
    {
        var details = new List<HealthDetail>();
        var actions = new List<HealthAction>();

        if (!hasRefreshToken)
        {
            return new SubsystemHealth(
                Key: "adsolut-sync",
                Label: "Sync",
                Status: HealthStatus.Ok,
                Summary: "Sync paused — no refresh token yet.",
                Details: new[] { new HealthDetail("State", "Waiting for first OAuth authorization.") },
                Actions: Array.Empty<HealthAction>());
        }

        if (connection?.AdministrationId is null)
        {
            return new SubsystemHealth(
                Key: "adsolut-sync",
                Label: "Sync",
                Status: HealthStatus.Warning,
                Summary: "No dossier picked yet — sync ticks are paused.",
                Details: new[] { new HealthDetail("State", "Pick an Adsolut administration on the integration page to start.") },
                Actions: Array.Empty<HealthAction>());
        }

        var now = DateTime.UtcNow;
        var ack = syncState?.AcknowledgedUtc;
        var hasUnackedError = !string.IsNullOrEmpty(syncState?.LastError)
            && IsAfter(syncState!.LastErrorUtc, ack);

        var staleThreshold = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes) * DefaultStaleIntervalMultiplier);
        var lastDelta = syncState?.LastDeltaSyncUtc;
        var isStale = lastDelta is { } d
            && (now - d) > staleThreshold
            && (ack is null || ack < d.Add(staleThreshold));

        HealthStatus status;
        string summary;

        if (hasUnackedError)
        {
            status = HealthStatus.Warning;
            summary = $"Last sync tick failed: {syncState!.LastError}.";
        }
        else if (isStale)
        {
            status = HealthStatus.Warning;
            summary = $"No successful sync in over {(int)staleThreshold.TotalMinutes} minutes.";
        }
        else if (lastDelta is null)
        {
            status = HealthStatus.Ok;
            summary = "Waiting for first sync tick…";
        }
        else
        {
            status = HealthStatus.Ok;
            summary = $"Last sync OK at {lastDelta.Value:u}.";
        }

        if (lastDelta is { } ld)
        {
            details.Add(new HealthDetail("Last delta sync", ld.ToString("u")));
        }
        if (syncState?.LastFullSyncUtc is { } lf)
        {
            details.Add(new HealthDetail("Last full sync", lf.ToString("u")));
        }
        var nextSync = ComputeNextSyncUtc(lastDelta, intervalMinutes);
        details.Add(new HealthDetail("Next sync", nextSync.ToString("u")));
        if (!string.IsNullOrEmpty(syncState?.LastError))
        {
            var when = syncState.LastErrorUtc is { } et ? et.ToString("u") : "(unknown time)";
            details.Add(new HealthDetail("Last error", $"{syncState.LastError} at {when}"));
        }
        if (ack is { } a)
        {
            details.Add(new HealthDetail("Acknowledged at", a.ToString("u")));
        }
        details.Add(new HealthDetail("Tick interval", $"{intervalMinutes} minute(s)"));

        // Acknowledge button shows up only when there is something to clear.
        // Once acked the row goes green; the next failed tick advances
        // LastErrorUtc past the ack and the button reappears.
        if (status != HealthStatus.Ok)
        {
            actions.Add(new HealthAction(
                Key: "ack-adsolut-sync",
                Label: "Acknowledge",
                Endpoint: "/api/admin/health/integrations/adsolut/sync/ack",
                ConfirmMessage: null));
        }

        return new SubsystemHealth(
            Key: "adsolut-sync",
            Label: "Sync",
            Status: status,
            Summary: summary,
            Details: details,
            Actions: actions);
    }

    /// True when <paramref name="value"/> is set and strictly later than
    /// <paramref name="threshold"/>; null threshold means "never seen", so
    /// any value is considered later.
    private static bool IsAfter(DateTime? value, DateTime? threshold)
    {
        if (value is null) return false;
        if (threshold is null) return true;
        return value.Value > threshold.Value;
    }

    /// Estimated next-tick wall-clock based on the last delta cursor and the
    /// configured interval. When the worker has never run yet we project
    /// "now" — the very first tick lands within the worker's stagger window
    /// (a few seconds), and showing "now" reads better than a synthetic
    /// past timestamp. Always returns a UTC value the SPA can format.
    public static DateTime ComputeNextSyncUtc(DateTime? lastDeltaUtc, int intervalMinutes)
    {
        var safeInterval = Math.Max(1, intervalMinutes);
        if (lastDeltaUtc is null) return DateTime.UtcNow;
        return lastDeltaUtc.Value.AddMinutes(safeInterval);
    }

    // ---- Telavox -------------------------------------------------------

    /// "Configured" definition mirrors <see cref="TelavoxPollingWorker"/>'s
    /// gate: only when the integration is opt-in (Enabled), a partner-token
    /// is stored and a customer-id is pinned do we surface the row at all.
    /// Any of these three missing → tile hidden, exactly like the polling
    /// worker silently skips. This keeps a freshly-installed instance from
    /// showing an empty Telavox card.
    private async Task<IntegrationHealth?> BuildTelavoxAsync(CancellationToken ct)
    {
        var enabled = await _settings.GetAsync<bool>(SettingKeys.Telavox.Enabled, ct);
        var hasPartnerToken = await _secrets.HasAsync(ProtectedSecretKeys.TelavoxPartnerToken, ct);
        var customerId = (await _settings.GetAsync<string>(SettingKeys.Telavox.PartnerCustomerId, ct)
            ?? string.Empty).Trim();
        if (!enabled || !hasPartnerToken || customerId.Length == 0)
        {
            return null;
        }

        // Idle interval is the slower of the two cadences; staleness is
        // judged against that since a worker stuck between rings would
        // still fall behind on the idle clock. Bounds mirror the worker's
        // own clamp so the health-card and the worker agree on the range.
        var pollInterval = Math.Clamp(
            await _settings.GetAsync<int>(SettingKeys.Telavox.PollIntervalSeconds, ct),
            2, 60);
        var windowStart = await _settings.GetAsync<string>(SettingKeys.Telavox.PollWindowStart, ct);
        var windowEnd = await _settings.GetAsync<string>(SettingKeys.Telavox.PollWindowEnd, ct);
        var windowDays = await _settings.GetAsync<string>(SettingKeys.Telavox.PollWindowDays, ct);
        var tzId = await _settings.GetAsync<string>(SettingKeys.App.TimeZone, ct);
        var tz = ResolveTimeZone(tzId);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        var inWindow = TelavoxPollWindow.IsInPollWindow(nowLocal, windowStart, windowEnd, windowDays);

        var links = await _telavoxLinks.ListAsync(ct);

        var connectionCheck = BuildTelavoxConnectionCheck(customerId, inWindow, windowStart, windowEnd);
        var pollingCheck = BuildTelavoxPollingCheck(links, pollInterval, inWindow);

        var checks = new[] { connectionCheck, pollingCheck };
        var rollup = checks.Aggregate(HealthStatus.Ok, (acc, c) => c.Status > acc ? c.Status : acc);

        return new IntegrationHealth(
            Key: "telavox",
            Name: "Telavox",
            LogoKey: "telavox",
            Rollup: rollup,
            Checks: checks,
            Actions: Array.Empty<HealthAction>());
    }

    /// Connection check covers the static side: the pinned customer-id +
    /// where the integration currently sits in its working-hours window. The
    /// row is always Ok once all three credentials are present (we gate on
    /// them above) — a token-revocation surfaces via a per-poll failure on
    /// the Polling check, not here, so the two rows don't double-flip.
    private static SubsystemHealth BuildTelavoxConnectionCheck(
        string customerId, bool inWindow, string windowStart, string windowEnd)
    {
        var details = new List<HealthDetail>
        {
            new("Partner customer-id", customerId),
            new("Working hours", $"{windowStart}–{windowEnd}"),
            new("Window state", inWindow ? "Inside working hours" : "Outside working hours"),
        };
        var summary = inWindow
            ? "Configured, polling inside working hours."
            : "Configured, currently outside working hours — polling is paused or slowed.";
        return new SubsystemHealth(
            Key: "telavox-connection",
            Label: "Connection",
            Status: HealthStatus.Ok,
            Summary: summary,
            Details: details,
            Actions: Array.Empty<HealthAction>());
    }

    /// Polling check rolls the per-link operational counters into one row:
    /// agents linked, last successful poll age, and any agents currently
    /// stuck on consecutive errors. We deliberately don't fire Warning for
    /// "no agents linked" — that's a valid steady-state for a fresh setup
    /// while the admin still maps Telavox extensions to SD users.
    private static SubsystemHealth BuildTelavoxPollingCheck(
        IReadOnlyList<TelavoxAgentLink> links,
        int pollIntervalSeconds,
        bool inWindow)
    {
        var details = new List<HealthDetail>
        {
            new("Agents linked", links.Count.ToString()),
            new("Poll interval", $"{pollIntervalSeconds} second(s)"),
        };

        if (links.Count == 0)
        {
            return new SubsystemHealth(
                Key: "telavox-polling",
                Label: "Polling",
                Status: HealthStatus.Ok,
                Summary: "Configured — no agents linked yet. Link agents on the integration page to start polling.",
                Details: details,
                Actions: Array.Empty<HealthAction>());
        }

        var errorLinks = links.Where(l => l.ConsecutiveErrors > 0).ToList();
        var lastPoll = links.Where(l => l.LastPollUtc is not null).Max(l => l.LastPollUtc);
        var anyPolled = lastPoll is not null;

        // "Stale" only matters when the worker should be actively polling
        // right now (inside the working-hours window). Outside hours the
        // gap is expected — flipping the row to Warning then would noise
        // the dashboard for the whole evening.
        var staleThreshold = TimeSpan.FromSeconds(Math.Max(pollIntervalSeconds * 4, 30));
        var isStale = inWindow && anyPolled
            && (DateTime.UtcNow - lastPoll!.Value) > staleThreshold;
        var neverPolledInWindow = inWindow && !anyPolled;

        HealthStatus status;
        string summary;
        if (errorLinks.Count == links.Count)
        {
            status = HealthStatus.Critical;
            summary = $"All {links.Count} linked agent(s) failing — verify the partner-token and per-agent CAPI tokens.";
        }
        else if (errorLinks.Count > 0)
        {
            status = HealthStatus.Warning;
            summary = $"{errorLinks.Count} of {links.Count} agent(s) failing — see the integration page for per-agent error codes.";
        }
        else if (isStale)
        {
            status = HealthStatus.Warning;
            summary = $"Polling has stalled — last successful tick was {Math.Floor((DateTime.UtcNow - lastPoll!.Value).TotalSeconds)}s ago.";
        }
        else if (neverPolledInWindow)
        {
            status = HealthStatus.Ok;
            summary = "Waiting for first poll tick…";
        }
        else if (!inWindow)
        {
            status = HealthStatus.Ok;
            summary = $"{links.Count} agent(s) linked, polling paused outside working hours.";
        }
        else
        {
            status = HealthStatus.Ok;
            summary = $"{links.Count} agent(s) linked, polling on schedule.";
        }

        if (lastPoll is { } lp)
        {
            details.Add(new HealthDetail("Last poll (any agent)", lp.ToString("u")));
        }
        if (errorLinks.Count > 0)
        {
            // Cap the list to avoid blowing up the tile on a many-agent
            // install. The integration page carries the full per-agent
            // detail; the dashboard row is a heads-up, not a panel.
            var sample = string.Join(", ",
                errorLinks.Take(3).Select(l => $"{l.TelavoxExtension} ({l.LastPollError ?? "error"})"));
            if (errorLinks.Count > 3) sample += $" + {errorLinks.Count - 3} more";
            details.Add(new HealthDetail("Failing agents", sample));
        }

        return new SubsystemHealth(
            Key: "telavox-polling",
            Label: "Polling",
            Status: status,
            Summary: summary,
            Details: details,
            Actions: Array.Empty<HealthAction>());
    }

    /// Same defensive resolver as <see cref="TelavoxPollingWorker"/> — keep
    /// the two in sync so the tile and the worker agree about which
    /// timezone the working-hours window is being evaluated in.
    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* Invalid IANA id — fall through. */ }
        }
        return TimeZoneInfo.Local;
    }
}
