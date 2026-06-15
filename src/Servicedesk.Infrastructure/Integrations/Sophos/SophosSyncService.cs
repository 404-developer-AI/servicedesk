using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Infrastructure.Integrations.Sophos;

/// Result of a Sophos sync pass, returned to the on-demand "Sync now" endpoint.
public sealed record SophosSyncOutcome(
    bool Success, string Status, string? Error, int TenantCount, int MailboxCount, bool Changed);

public interface ISophosSyncService
{
    /// Pull the partner tenant list, reconcile the local snapshot, and refresh
    /// the protected mailbox set for every M365-connected tenant. Never throws
    /// for an upstream problem — classifies it onto the sync state. Concurrent
    /// calls (worker + manual) coalesce: the second returns a "busy" outcome.
    Task<SophosSyncOutcome> SyncAsync(CancellationToken ct);
}

public sealed class SophosSyncService : ISophosSyncService
{
    // Pause between per-tenant mailbox calls so a wide partner doesn't hammer
    // Sophos. Small enough to keep a full pass quick on ~100 tenants.
    private static readonly TimeSpan PerTenantDelay = TimeSpan.FromMilliseconds(150);

    public const string StatusOk = "ok";
    public const string StatusAuth = "credentials_error";
    public const string StatusError = "error";

    private readonly ISophosStore _store;
    private readonly ISophosApiClient _client;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<SophosSyncService> _logger;

    private int _running;

    public SophosSyncService(
        ISophosStore store,
        ISophosApiClient client,
        IIntegrationAuditLogger audit,
        ILogger<SophosSyncService> logger)
    {
        _store = store;
        _client = client;
        _audit = audit;
        _logger = logger;
    }

    public async Task<SophosSyncOutcome> SyncAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new SophosSyncOutcome(false, "busy", "A Sophos sync is already running.", 0, 0, false);

        try
        {
            return await RunAsync(ct);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<SophosSyncOutcome> RunAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var tenantsResult = await _client.ListTenantsAsync(ct);
        if (!tenantsResult.Success)
        {
            sw.Stop();
            var status = tenantsResult.AuthFailure ? StatusAuth : StatusError;
            var prior = await _store.GetSyncStateAsync(ct);
            await _store.SaveSyncStateAsync(new SophosSyncStateRow
            {
                LastCheckedUtc = nowUtc,
                LastChangedUtc = prior?.LastChangedUtc,
                LastStatus = status,
                LastError = tenantsResult.Error,
                TenantCount = prior?.TenantCount ?? 0,
                MailboxCount = prior?.MailboxCount ?? 0,
                ContentHash = prior?.ContentHash,
                DurationMs = (int)sw.ElapsedMilliseconds,
            }, ct);
            await LogTickAsync(status, tenantsResult.Error, 0, 0, false, (int)sw.ElapsedMilliseconds, ct);
            return new SophosSyncOutcome(false, status, tenantsResult.Error,
                prior?.TenantCount ?? 0, prior?.MailboxCount ?? 0, false);
        }

        var tenants = tenantsResult.Tenants;
        await _store.ReplaceTenantsAsync(tenants, nowUtc, ct);

        // Pull mailboxes only for tenants matched to an M365-connected company.
        var targets = await _store.GetMailboxTargetsAsync(ct);
        var keepTenantIds = targets.Select(t => t.TenantId).ToList();
        await _store.ClearMailboxesExceptAsync(keepTenantIds, ct);

        var mailboxEmails = new List<(string TenantId, string Email)>();
        var totalMailboxes = 0;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var mbResult = await _client.ListMailboxesAsync(target.TenantId, target.ApiHost, ct);
            if (mbResult.Success)
            {
                await _store.ReplaceMailboxesForTenantAsync(target.TenantId, mbResult.Mailboxes, nowUtc, ct);
                totalMailboxes += mbResult.Mailboxes.Count;
                foreach (var m in mbResult.Mailboxes)
                    mailboxEmails.Add((target.TenantId, m.Email));
            }
            else
            {
                // Keep the prior mailbox snapshot for this tenant; log and move on
                // so one bad tenant never sinks the whole pass.
                _logger.LogWarning(
                    "Sophos mailbox read for tenant {TenantId} failed ({Error}); keeping prior snapshot.",
                    target.TenantId, mbResult.Error);
            }

            if (PerTenantDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(PerTenantDelay, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            }
        }

        sw.Stop();

        var hash = ComputeHash(tenants, mailboxEmails);
        var prior2 = await _store.GetSyncStateAsync(ct);
        var changed = prior2?.ContentHash != hash;

        await _store.SaveSyncStateAsync(new SophosSyncStateRow
        {
            LastCheckedUtc = nowUtc,
            LastChangedUtc = changed ? nowUtc : prior2?.LastChangedUtc,
            LastStatus = StatusOk,
            LastError = null,
            TenantCount = tenants.Count,
            MailboxCount = totalMailboxes,
            ContentHash = hash,
            DurationMs = (int)sw.ElapsedMilliseconds,
        }, ct);

        await LogTickAsync(StatusOk, null, tenants.Count, totalMailboxes, changed, (int)sw.ElapsedMilliseconds, ct);
        return new SophosSyncOutcome(true, StatusOk, null, tenants.Count, totalMailboxes, changed);
    }

    private Task LogTickAsync(
        string status, string? error, int tenantCount, int mailboxCount, bool changed, int latencyMs, CancellationToken ct) =>
        _audit.LogAsync(new IntegrationAuditEvent(
            Integration: SophosEventTypes.Integration,
            EventType: SophosEventTypes.SyncTick,
            Outcome: status == StatusOk ? IntegrationAuditOutcome.Ok : IntegrationAuditOutcome.Warn,
            LatencyMs: latencyMs,
            ErrorCode: error is null ? null : status,
            Payload: new { status, tenantCount, mailboxCount, changed }), ct);

    /// Stable hash over the tenant snapshot + the gathered protected mailbox
    /// addresses, so an unchanged sync keeps its last_changed timestamp.
    /// Order-independent: both lists are sorted first.
    private static string ComputeHash(
        IReadOnlyList<SophosTenantRow> tenants, IReadOnlyList<(string TenantId, string Email)> emails)
    {
        var sb = new StringBuilder(tenants.Count * 64 + emails.Count * 48);
        foreach (var t in tenants.OrderBy(t => t.TenantId, StringComparer.Ordinal))
        {
            sb.Append(t.TenantId).Append('')
              .Append(t.ShowAs).Append('')
              .Append(t.CompanyCode).Append('')
              .Append(t.Status).Append('')
              .Append(t.ApiHost).Append('');
        }
        foreach (var e in emails
                     .OrderBy(e => e.TenantId, StringComparer.Ordinal)
                     .ThenBy(e => e.Email, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(e.TenantId).Append('').Append(e.Email.ToLowerInvariant()).Append('');
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }
}
