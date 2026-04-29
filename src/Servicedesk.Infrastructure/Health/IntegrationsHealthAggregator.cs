using Servicedesk.Infrastructure.Integrations.Adsolut;
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

    public IntegrationsHealthAggregator(
        IProtectedSecretStore secrets,
        ISettingsService settings,
        IAdsolutConnectionStore adsolutConnections,
        IAdsolutSyncStateStore adsolutSyncState)
    {
        _secrets = secrets;
        _settings = settings;
        _adsolutConnections = adsolutConnections;
        _adsolutSyncState = adsolutSyncState;
    }

    public async Task<IntegrationsHealthReport> CollectAsync(CancellationToken ct)
    {
        var integrations = new List<IntegrationHealth>();

        var adsolut = await BuildAdsolutAsync(ct);
        if (adsolut is not null) integrations.Add(adsolut);

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
}
