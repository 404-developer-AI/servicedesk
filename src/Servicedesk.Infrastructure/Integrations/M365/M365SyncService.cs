using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Infrastructure.Integrations.M365;

/// Outcome of syncing one company, returned to the on-demand "Sync now"
/// endpoint so it can report success/failure to the agent.
public sealed record M365SyncOutcome(bool Success, string Status, string? Error, int MailboxCount, bool Changed);

public interface IM365SyncService
{
    /// Read one connected tenant and reconcile the local mirror. Never throws
    /// for an upstream problem — classifies it onto the link + sync state.
    Task<M365SyncOutcome> SyncCompanyAsync(Guid companyId, CancellationToken ct);

    /// Sync every active link in turn (used by the worker).
    Task SyncAllAsync(CancellationToken ct);
}

public sealed class M365SyncService : IM365SyncService
{
    private readonly IM365TenantStore _store;
    private readonly IM365GraphReader _reader;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<M365SyncService> _logger;

    public M365SyncService(
        IM365TenantStore store,
        IM365GraphReader reader,
        IIntegrationAuditLogger audit,
        ILogger<M365SyncService> logger)
    {
        _store = store;
        _reader = reader;
        _audit = audit;
        _logger = logger;
    }

    public async Task SyncAllAsync(CancellationToken ct)
    {
        var links = await _store.GetActiveLinksAsync(ct);
        foreach (var link in links)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SyncCompanyAsync(link.CompanyId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad tenant must not abort the whole pass.
                _logger.LogWarning(ex, "M365 sync for company {CompanyId} threw; continuing.", link.CompanyId);
            }
        }
    }

    public async Task<M365SyncOutcome> SyncCompanyAsync(Guid companyId, CancellationToken ct)
    {
        var link = await _store.GetLinkAsync(companyId, ct);
        if (link is null)
            return new M365SyncOutcome(false, "not_linked", "No Microsoft 365 connection for this company.", 0, false);

        var nowUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var result = await _reader.ReadMailboxesAsync(link.TenantId, ct);
        sw.Stop();

        if (!result.Success)
        {
            var status = result.AuthFailure ? M365LinkStatus.NeedsReconsent : M365LinkStatus.Error;
            await _store.SetLinkStatusAsync(companyId, status, result.Error, verifiedUtc: null, nowUtc, ct);

            var prior = await _store.GetSyncStateAsync(companyId, ct);
            await _store.SaveSyncStateAsync(new M365CompanySyncRow
            {
                CompanyId = companyId,
                LastCheckedUtc = nowUtc,
                LastChangedUtc = prior?.LastChangedUtc,
                LastStatus = status,
                LastError = result.Error,
                MailboxCount = prior?.MailboxCount ?? 0,
                ContentHash = prior?.ContentHash,
                DurationMs = (int)sw.ElapsedMilliseconds,
            }, ct);

            await LogTickAsync(companyId, status, result.Error, 0, changed: false, (int)sw.ElapsedMilliseconds, ct);
            return new M365SyncOutcome(false, status, result.Error, prior?.MailboxCount ?? 0, false);
        }

        var hash = ComputeHash(result.Mailboxes);
        var existing = await _store.GetSyncStateAsync(companyId, ct);
        var changed = existing?.ContentHash != hash;

        if (changed)
        {
            await _store.ReplaceMailboxesAsync(companyId, result.Mailboxes, nowUtc, ct);
        }
        await _store.SetLinkStatusAsync(companyId, M365LinkStatus.Connected, error: null, verifiedUtc: nowUtc, nowUtc, ct);
        await _store.SaveSyncStateAsync(new M365CompanySyncRow
        {
            CompanyId = companyId,
            LastCheckedUtc = nowUtc,
            LastChangedUtc = changed ? nowUtc : existing?.LastChangedUtc,
            LastStatus = M365LinkStatus.Connected,
            LastError = null,
            MailboxCount = result.Mailboxes.Count,
            ContentHash = hash,
            DurationMs = (int)sw.ElapsedMilliseconds,
        }, ct);

        await LogTickAsync(companyId, M365LinkStatus.Connected, null, result.Mailboxes.Count, changed, (int)sw.ElapsedMilliseconds, ct);
        return new M365SyncOutcome(true, M365LinkStatus.Connected, null, result.Mailboxes.Count, changed);
    }

    private Task LogTickAsync(
        Guid companyId, string status, string? error, int mailboxCount, bool changed, int latencyMs, CancellationToken ct) =>
        _audit.LogAsync(new IntegrationAuditEvent(
            Integration: M365EventTypes.Integration,
            EventType: M365EventTypes.SyncTick,
            Outcome: status == M365LinkStatus.Connected ? IntegrationAuditOutcome.Ok : IntegrationAuditOutcome.Warn,
            LatencyMs: latencyMs,
            ErrorCode: error is null ? null : status,
            // companyId is an internal id, not sensitive; the tenant id is NOT logged.
            Payload: new { companyId, status, mailboxCount, changed }), ct);

    /// Stable hash over the mailbox set so an unchanged tenant skips the rewrite
    /// and keeps its last_changed timestamp. Order-independent: rows are sorted
    /// by object id first.
    private static string ComputeHash(IReadOnlyList<M365MailboxRow> rows)
    {
        var sb = new StringBuilder(rows.Count * 64);
        foreach (var r in rows.OrderBy(r => r.ObjectId, StringComparer.Ordinal))
        {
            sb.Append(r.ObjectId).Append('')
              .Append(r.MailboxType).Append('')
              .Append(r.DisplayName).Append('')
              .Append(r.GivenName).Append('')
              .Append(r.Surname).Append('')
              .Append(r.Upn).Append('')
              .Append(r.Mail).Append('')
              .Append(r.Enabled).Append('')
              .Append(r.Licenses).Append('');
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }
}
