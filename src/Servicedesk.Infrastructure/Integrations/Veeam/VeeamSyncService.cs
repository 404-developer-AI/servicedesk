using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Veeam;

/// Result of a Veeam sync pass, returned to the on-demand "Sync now" endpoint.
public sealed record VeeamSyncOutcome(
    bool Success, string Status, string? Error, int CompanyCount, int ObjectCount, bool Changed);

public interface IVeeamSyncService
{
    /// Read VSPC, match its companies to the M365-connected companies by relation
    /// code, and refresh each matched company's per-mailbox backup snapshot.
    /// Never throws for an upstream problem — classifies it onto the sync state.
    /// Concurrent calls (worker + manual) coalesce: the second returns "busy".
    Task<VeeamSyncOutcome> SyncAsync(CancellationToken ct);
}

public sealed class VeeamSyncService : IVeeamSyncService
{
    // Pause between per-company protected-object calls so a wide console isn't
    // hammered. Small enough to keep a full pass quick on ~100 companies.
    private static readonly TimeSpan PerCompanyDelay = TimeSpan.FromMilliseconds(150);

    public const string StatusOk = "ok";
    public const string StatusAuth = "credentials_error";
    public const string StatusError = "error";
    public const string StatusNotConfigured = "not_configured";

    private readonly IVeeamStore _store;
    private readonly IVeeamApiClient _client;
    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<VeeamSyncService> _logger;

    private int _running;

    public VeeamSyncService(
        IVeeamStore store,
        IVeeamApiClient client,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IIntegrationAuditLogger audit,
        ILogger<VeeamSyncService> logger)
    {
        _store = store;
        _client = client;
        _settings = settings;
        _secrets = secrets;
        _audit = audit;
        _logger = logger;
    }

    public async Task<VeeamSyncOutcome> SyncAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new VeeamSyncOutcome(false, "busy", "A Veeam sync is already running.", 0, 0, false);

        try
        {
            return await RunAsync(ct);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<VeeamSyncOutcome> RunAsync(CancellationToken ct)
    {
        var conn = await BuildConnectionAsync(ct);
        if (conn is null)
            return new VeeamSyncOutcome(false, StatusNotConfigured,
                "Set the base URL, username and password before syncing.", 0, 0, false);

        var nowUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var companiesResult = await _client.ListCompaniesAsync(conn, ct);
        if (!companiesResult.Success)
        {
            sw.Stop();
            var status = companiesResult.AuthFailure ? StatusAuth : StatusError;
            var prior = await _store.GetSyncStateAsync(ct);
            await _store.SaveSyncStateAsync(new VeeamSyncStateRow
            {
                LastCheckedUtc = nowUtc,
                LastChangedUtc = prior?.LastChangedUtc,
                LastStatus = status,
                LastError = companiesResult.Error,
                CompanyCount = prior?.CompanyCount ?? 0,
                ObjectCount = prior?.ObjectCount ?? 0,
                ContentHash = prior?.ContentHash,
                DurationMs = (int)sw.ElapsedMilliseconds,
            }, ct);
            await LogTickAsync(status, companiesResult.Error, 0, 0, false, (int)sw.ElapsedMilliseconds, ct);
            return new VeeamSyncOutcome(false, status, companiesResult.Error,
                prior?.CompanyCount ?? 0, prior?.ObjectCount ?? 0, false);
        }

        // Snapshot every VSPC company (the pick list for the link UI).
        await _store.ReplaceVspcCompaniesAsync(companiesResult.Companies, nowUtc, ct);

        var vspcByUid = companiesResult.Companies
            .Where(c => !string.IsNullOrWhiteSpace(c.InstanceUid))
            .GroupBy(c => c.InstanceUid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Match VSPC companies to M365-connected companies by relation code.
        var targets = await _store.GetSyncTargetsAsync(ct);
        var targetByCode = targets
            .GroupBy(t => t.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().CompanyId, StringComparer.OrdinalIgnoreCase);

        // Per parent company: the ordered, de-duplicated VSPC source uids whose
        // protected objects fold into the parent's backup set. The automatic
        // (code) match is the primary source; the manual links add the daughter
        // companies on top. primaryByCompany prefers the automatic match's uid
        // for the stored vspc identity, falling back to the first linked uid.
        var sourcesByCompany = new Dictionary<Guid, List<string>>();
        var primaryByCompany = new Dictionary<Guid, string>();

        void AddSource(Guid cid, string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return;
            if (!sourcesByCompany.TryGetValue(cid, out var list))
            {
                list = new List<string>();
                sourcesByCompany[cid] = list;
            }
            if (!list.Contains(uid, StringComparer.Ordinal)) list.Add(uid);
        }

        foreach (var c in companiesResult.Companies)
        {
            if (string.IsNullOrWhiteSpace(c.Code) || string.IsNullOrWhiteSpace(c.InstanceUid)) continue;
            if (!targetByCode.TryGetValue(c.Code!.Trim(), out var matchedId)) continue;
            AddSource(matchedId, c.InstanceUid);
            primaryByCompany.TryAdd(matchedId, c.InstanceUid);
        }

        var links = await _store.GetCompanyLinksAsync(ct);
        foreach (var link in links)
            AddSource(link.ParentCompanyId, link.VspcCompanyUid);

        var keepCompanyIds = new List<Guid>();
        var totalObjects = 0;
        var hashParts = new List<string>();

        // Cache object reads by uid so a VSPC company linked to several parents
        // is pulled (and throttled) once per pass.
        var objectsCache = new Dictionary<string, VeeamProtectedObjectsResult>(StringComparer.Ordinal);

        async Task<VeeamProtectedObjectsResult?> ReadObjectsAsync(string uid)
        {
            if (objectsCache.TryGetValue(uid, out var cached)) return cached;
            // A linked uid that has left the console is no longer in the
            // snapshot — skip it (null) rather than failing the parent.
            if (!vspcByUid.ContainsKey(uid)) return null;
            var result = await _client.ListProtectedObjectsAsync(conn, uid, ct);
            objectsCache[uid] = result;
            if (PerCompanyDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(PerCompanyDelay, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            }
            return result;
        }

        foreach (var (companyId, sourceUids) in sourcesByCompany)
        {
            ct.ThrowIfCancellationRequested();

            var combined = new List<VeeamProtectedObjectRow>();
            var readFailed = false;
            foreach (var uid in sourceUids)
            {
                var objectsResult = await ReadObjectsAsync(uid);
                if (objectsResult is null) continue; // stale linked company
                if (!objectsResult.Success) { readFailed = true; break; }
                combined.AddRange(objectsResult.Objects);
            }

            if (readFailed)
            {
                // Keep the prior snapshot for this company; one bad read never
                // sinks the whole pass nor drops a sibling source's rows.
                _logger.LogWarning(
                    "Veeam protected-objects read for company {CompanyId} failed; keeping prior snapshot.",
                    companyId);
                keepCompanyIds.Add(companyId);
                continue;
            }

            var storedUid = primaryByCompany.TryGetValue(companyId, out var p) ? p : sourceUids[0];
            var storedName = vspcByUid.TryGetValue(storedUid, out var vc) ? vc.Name : null;

            var rows = BuildBackupRows(combined);
            await _store.ReplaceCompanyBackupsAsync(companyId, storedUid, storedName, rows, nowUtc, ct);
            keepCompanyIds.Add(companyId);
            totalObjects += combined.Count;

            foreach (var r in rows.OrderBy(r => r.MatchKey, StringComparer.Ordinal))
            {
                hashParts.Add(string.Join('',
                    companyId.ToString(), r.MatchKey,
                    r.ExchangeProtected ? "1" : "0", r.ExchangeRestorePoints?.ToString() ?? "",
                    r.ExchangeLastBackupUtc?.ToString("O") ?? "",
                    r.OneDriveProtected ? "1" : "0", r.OneDriveRestorePoints?.ToString() ?? "",
                    r.OneDriveLastBackupUtc?.ToString("O") ?? ""));
            }
        }

        // Drop companies that no longer match or link to a VSPC company
        // (cascades backups).
        await _store.ClearCompaniesExceptAsync(keepCompanyIds, ct);

        sw.Stop();

        var companyCount = keepCompanyIds.Count;
        var hash = ComputeHash(hashParts);
        var prior2 = await _store.GetSyncStateAsync(ct);
        var changed = prior2?.ContentHash != hash;

        await _store.SaveSyncStateAsync(new VeeamSyncStateRow
        {
            LastCheckedUtc = nowUtc,
            LastChangedUtc = changed ? nowUtc : prior2?.LastChangedUtc,
            LastStatus = StatusOk,
            LastError = null,
            CompanyCount = companyCount,
            ObjectCount = totalObjects,
            ContentHash = hash,
            DurationMs = (int)sw.ElapsedMilliseconds,
        }, ct);

        await LogTickAsync(StatusOk, null, companyCount, totalObjects, changed, (int)sw.ElapsedMilliseconds, ct);
        return new VeeamSyncOutcome(true, StatusOk, null, companyCount, totalObjects, changed);
    }

    /// Group the protected objects of one company into per-mailbox rows keyed by
    /// the normalized display name. Exchange and OneDrive objects fold into the
    /// same row; duplicates keep the highest restore-point count and latest date.
    private static List<VeeamBackupRow> BuildBackupRows(IReadOnlyList<VeeamProtectedObjectRow> objects)
    {
        var map = new Dictionary<string, VeeamBackupRow>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            var key = VeeamMatch.Key(o.Name);
            if (key is null) continue;
            if (!o.IsExchange && !o.IsOneDrive) continue;

            if (!map.TryGetValue(key, out var row))
            {
                row = new VeeamBackupRow { MatchKey = key, DisplayName = o.Name?.Trim() };
                map[key] = row;
            }

            if (o.IsExchange)
            {
                row.ExchangeProtected = true;
                row.ExchangeRestorePoints = Math.Max(row.ExchangeRestorePoints ?? 0, o.RestorePointsCount);
                row.ExchangeLastBackupUtc = Later(row.ExchangeLastBackupUtc, o.LatestRestorePointDate);
            }
            if (o.IsOneDrive)
            {
                row.OneDriveProtected = true;
                row.OneDriveRestorePoints = Math.Max(row.OneDriveRestorePoints ?? 0, o.RestorePointsCount);
                row.OneDriveLastBackupUtc = Later(row.OneDriveLastBackupUtc, o.LatestRestorePointDate);
            }
        }
        return map.Values.ToList();
    }

    private static DateTime? Later(DateTime? a, DateTime? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a > b ? a : b;
    }

    private async Task<VeeamConnection?> BuildConnectionAsync(CancellationToken ct)
    {
        var baseUrl = (await _settings.GetAsync<string>(SettingKeys.Veeam.BaseUrl, ct) ?? string.Empty).Trim().TrimEnd('/');
        var username = (await _settings.GetAsync<string>(SettingKeys.Veeam.Username, ct) ?? string.Empty).Trim();
        var password = await _secrets.GetAsync(ProtectedSecretKeys.VeeamPassword, ct);
        var allowSelfSigned = await _settings.GetAsync<bool>(SettingKeys.Veeam.AllowSelfSignedTls, ct);

        if (baseUrl.Length == 0 || username.Length == 0 || string.IsNullOrEmpty(password))
            return null;
        return new VeeamConnection(baseUrl, username, password, allowSelfSigned);
    }

    private Task LogTickAsync(
        string status, string? error, int companyCount, int objectCount, bool changed, int latencyMs, CancellationToken ct) =>
        _audit.LogAsync(new IntegrationAuditEvent(
            Integration: VeeamEventTypes.Integration,
            EventType: VeeamEventTypes.SyncTick,
            Outcome: status == StatusOk ? IntegrationAuditOutcome.Ok : IntegrationAuditOutcome.Warn,
            LatencyMs: latencyMs,
            ErrorCode: error is null ? null : status,
            Payload: new { status, companyCount, objectCount, changed }), ct);

    /// Stable hash over the per-mailbox backup rows so an unchanged sync keeps
    /// its last_changed timestamp. The parts are sorted for order-independence.
    private static string ComputeHash(List<string> parts)
    {
        parts.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder(parts.Count * 64);
        foreach (var p in parts) sb.Append(p).Append('');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }
}
