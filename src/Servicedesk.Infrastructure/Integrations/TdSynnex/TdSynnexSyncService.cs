using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

public sealed record TdSynnexSyncOutcome(
    bool Success, string Status, string? Error, int Seen, int Upserted, int Changed);

public interface ITdSynnexSyncService
{
    /// One sync pass: the delta (orders modified since the cursor; on the
    /// very first pass: created in the last <c>InitialSyncDays</c>) plus, when
    /// due or <paramref name="forceActiveRefresh"/>, a full re-read of every
    /// active order. Never throws for an upstream problem. Concurrent calls
    /// (worker + "Sync now") coalesce: the second returns "busy".
    Task<TdSynnexSyncOutcome> SyncAsync(bool forceActiveRefresh, CancellationToken ct);
}

public sealed class TdSynnexSyncService : ITdSynnexSyncService
{
    public const string StatusOk = "ok";
    public const string StatusAuth = "credentials_error";
    public const string StatusError = "error";
    public const string StatusBusy = "busy";

    /// The delta re-reads this far before the cursor, so an order modified
    /// while the previous pass was running is never missed. Unchanged orders
    /// in the overlap are a cheap hash compare.
    private static readonly TimeSpan CursorOverlap = TimeSpan.FromMinutes(15);

    private readonly ITdSynnexApiClient _client;
    private readonly ITdSynnexOrderRepository _repo;
    private readonly ITdSynnexOrderEvents _events;
    private readonly ITdSynnexOrderTicketFanout _fanout;
    private readonly ISettingsService _settings;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<TdSynnexSyncService> _logger;

    private int _running;

    public TdSynnexSyncService(
        ITdSynnexApiClient client,
        ITdSynnexOrderRepository repo,
        ITdSynnexOrderEvents events,
        ITdSynnexOrderTicketFanout fanout,
        ISettingsService settings,
        IIntegrationAuditLogger audit,
        ILogger<TdSynnexSyncService> logger)
    {
        _client = client;
        _repo = repo;
        _events = events;
        _fanout = fanout;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<TdSynnexSyncOutcome> SyncAsync(bool forceActiveRefresh, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new TdSynnexSyncOutcome(false, StatusBusy, "A TD SYNNEX sync is already running.", 0, 0, 0);
        try
        {
            return await RunAsync(forceActiveRefresh, ct);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<TdSynnexSyncOutcome> RunAsync(bool forceActiveRefresh, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var prior = await _repo.GetSyncStateAsync(ct) ?? new TdSynnexSyncStateRow();

        var initialDays = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.TdSynnex.InitialSyncDays, ct), 1, 730);
        var activeEvery = TimeSpan.FromMinutes(Math.Max(60, await _settings.GetAsync<int>(SettingKeys.TdSynnex.ActiveRefreshMinutes, ct)));
        var retentionDays = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.TdSynnex.ChangeRetentionDays, ct), 30, 3650);

        var deltaQuery = prior.DeltaCursorUtc is { } cursor
            ? new TdSynnexOrderQuery(ModifiedFromUtc: cursor - CursorOverlap)
            : new TdSynnexOrderQuery(CreatedFromUtc: nowUtc.AddDays(-initialDays));
        var doActive = forceActiveRefresh
            || prior.LastActiveRefreshUtc is null
            || nowUtc - prior.LastActiveRefreshUtc.Value >= activeEvery;

        var delta = await _client.ListOrdersAsync(deltaQuery, ct);
        if (!delta.Success)
            return await FailAsync(prior, nowUtc, sw, delta, ct);

        var merged = new Dictionary<string, TdSynnexOrder>(StringComparer.Ordinal);
        foreach (var o in delta.Orders) merged[o.OrderNumber] = o;

        if (doActive)
        {
            var active = await _client.ListOrdersAsync(new TdSynnexOrderQuery(Statuses: TdSynnexStatus.Active), ct);
            if (!active.Success)
                return await FailAsync(prior, nowUtc, sw, active, ct);
            foreach (var o in active.Orders) merged[o.OrderNumber] = o;
        }

        var (upserted, changed) = await ApplyAsync(merged.Values.ToList(), nowUtc, ct);

        try { await _repo.PurgeChangesAsync(nowUtc.AddDays(-retentionDays), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TD SYNNEX change-history purge failed.");
        }

        sw.Stop();
        await _repo.SaveSyncStateAsync(new TdSynnexSyncStateRow
        {
            // The cursor moves to the start of this pass only on success.
            DeltaCursorUtc = nowUtc,
            LastActiveRefreshUtc = doActive ? nowUtc : prior.LastActiveRefreshUtc,
            LastCheckedUtc = nowUtc,
            LastSuccessUtc = nowUtc,
            LastStatus = StatusOk,
            LastError = null,
            LastSeen = merged.Count,
            LastUpserted = upserted,
            LastChanged = changed,
            DurationMs = (int)sw.ElapsedMilliseconds,
        }, ct);
        await LogTickAsync(StatusOk, null, merged.Count, upserted, changed, doActive, delta.ListKey, (int)sw.ElapsedMilliseconds, ct);
        return new TdSynnexSyncOutcome(true, StatusOk, null, merged.Count, upserted, changed);
    }

    /// Diff every incoming order against the mirror, upsert it, record the
    /// change history and run the order pass on linked tickets. Per-order
    /// try/catch: one malformed order never sinks the pass.
    private async Task<(int Upserted, int Changed)> ApplyAsync(
        IReadOnlyList<TdSynnexOrder> orders, DateTime nowUtc, CancellationToken ct)
    {
        var upserted = 0;
        var changedCount = 0;
        foreach (var chunk in orders.Chunk(200))
        {
            var stored = await _repo.GetStoredAsync(chunk.Select(o => o.OrderNumber).ToList(), ct);
            foreach (var order in chunk)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var derived = TdSynnexOrderDiff.Derive(order);
                    IReadOnlyList<TdSynnexOrderChange> changes = Array.Empty<TdSynnexOrderChange>();
                    string? previousStatus = null;
                    if (stored.TryGetValue(order.OrderNumber, out var existing)
                        && (existing.SnapshotHash is null || !existing.SnapshotHash.AsSpan().SequenceEqual(derived.SnapshotHash)))
                    {
                        changes = TdSynnexOrderDiff.Compare(existing.Order, order);
                        previousStatus = existing.Order.Status;
                    }

                    var orderId = await _repo.UpsertAsync(order, derived, changes.Count > 0, nowUtc, ct);
                    upserted++;
                    if (changes.Count == 0) continue;

                    changedCount++;
                    await _repo.AddChangeAsync(orderId, nowUtc, TdSynnexOrderDiff.Summarize(order.OrderNumber, changes), changes, ct);

                    var evt = await _events.BuildAsync(orderId, order, previousStatus, changes,
                        derived.FirstShipDate, derived.EtaDate, ct);
                    await _fanout.RunAsync(evt, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "TD SYNNEX order {OrderNumber} could not be applied.", order.OrderNumber);
                }
            }
        }
        return (upserted, changedCount);
    }

    private async Task<TdSynnexSyncOutcome> FailAsync(
        TdSynnexSyncStateRow prior, DateTime nowUtc, Stopwatch sw, TdSynnexOrdersResult result, CancellationToken ct)
    {
        sw.Stop();
        var status = result.AuthFailure ? StatusAuth : StatusError;
        await _repo.SaveSyncStateAsync(new TdSynnexSyncStateRow
        {
            DeltaCursorUtc = prior.DeltaCursorUtc,
            LastActiveRefreshUtc = prior.LastActiveRefreshUtc,
            LastCheckedUtc = nowUtc,
            LastSuccessUtc = prior.LastSuccessUtc,
            LastStatus = status,
            LastError = result.Error,
            LastSeen = 0,
            LastUpserted = 0,
            LastChanged = 0,
            DurationMs = (int)sw.ElapsedMilliseconds,
        }, ct);
        await LogTickAsync(status, result.Error, 0, 0, 0, false, null, (int)sw.ElapsedMilliseconds, ct);
        return new TdSynnexSyncOutcome(false, status, result.Error, 0, 0, 0);
    }

    private Task LogTickAsync(
        string status, string? error, int seen, int upserted, int changed, bool activeRefresh,
        string? listKey, int latencyMs, CancellationToken ct) =>
        _audit.LogAsync(new IntegrationAuditEvent(
            Integration: TdSynnexEventTypes.Integration,
            EventType: TdSynnexEventTypes.SyncTick,
            Outcome: status == StatusOk ? IntegrationAuditOutcome.Ok : IntegrationAuditOutcome.Warn,
            LatencyMs: latencyMs,
            ErrorCode: error is null ? null : status,
            Payload: new { status, seen, upserted, changed, activeRefresh, listKey }), ct);
}
