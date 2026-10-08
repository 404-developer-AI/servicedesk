using System.Text.Json;
using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Infrastructure.Insights.Rewind;

public sealed record RewindViewSummary(Guid Id, string Name);

public sealed record RewindSlot(DateTime T, bool Covered, IReadOnlyDictionary<string, int> Counts);

public sealed record RewindSeries(
    int IntervalMinutes, DateTime FromUtc, DateTime ToUtc, DateTime LatestSlotUtc,
    IReadOnlyList<RewindGroup> Groups, IReadOnlyList<RewindSlot> Slots);

public sealed record RewindSnapshot(
    DateTime SlotUtc, bool Covered, DateTime? CapturedUtc, bool Truncated,
    IReadOnlyList<RewindGroup> Groups, IReadOnlyList<RewindItem> Items, IReadOnlyList<Guid> DeletedIds);

/// Read side of Rewind. Every result is cut to the caller's queue access
/// here, on the server: chart counts sum only visible queues, and a group
/// header appears only when it holds at least one visible ticket (a queue,
/// assignee, requester or company group label is itself data).
public interface IRewindService
{
    /// Hard guard on a chart window (8 days of slots), not a tunable.
    public static readonly TimeSpan MaxSeriesSpan = TimeSpan.FromDays(8);

    Task<IReadOnlyList<RewindViewSummary>> ListViewsAsync(Guid userId, string role, CancellationToken ct = default);
    Task<bool> CanReadViewAsync(Guid userId, string role, Guid viewId, CancellationToken ct = default);
    Task<int> GetIntervalAsync(CancellationToken ct = default);
    /// Slots from <c>end − span</c> to <c>end</c>; <paramref name="endUtc"/>
    /// null or past the latest slot = the latest slot (server time).
    Task<RewindSeries> GetSeriesAsync(Guid viewId, DateTime? endUtc, TimeSpan span, QueueAccessScope scope, CancellationToken ct = default);
    Task<RewindSnapshot> GetSnapshotAsync(Guid viewId, DateTime atUtc, QueueAccessScope scope, CancellationToken ct = default);
}

public sealed class RewindService : IRewindService
{
    private readonly IRewindStore _store;
    private readonly IViewAccessService _viewAccess;
    private readonly Settings.ISettingsService _settings;
    private readonly TimeProvider _time;

    public RewindService(IRewindStore store, IViewAccessService viewAccess, Settings.ISettingsService settings, TimeProvider time)
    {
        _store = store;
        _viewAccess = viewAccess;
        _settings = settings;
        _time = time;
    }

    public async Task<IReadOnlyList<RewindViewSummary>> ListViewsAsync(Guid userId, string role, CancellationToken ct = default)
    {
        var tracked = await _store.ListTrackedViewsAsync(ct);
        if (tracked.Count == 0) return Array.Empty<RewindViewSummary>();
        if (!IsAdmin(role))
        {
            var accessible = (await _viewAccess.GetAccessibleViewsAsync(userId, role, ct)).Select(v => v.Id).ToHashSet();
            tracked = tracked.Where(v => accessible.Contains(v.Id)).ToList();
        }
        return tracked.Select(v => new RewindViewSummary(v.Id, v.Name)).ToList();
    }

    public async Task<bool> CanReadViewAsync(Guid userId, string role, Guid viewId, CancellationToken ct = default)
    {
        // Tracked-ness is not checked: history of a view that has since been
        // untracked stays readable until retention prunes it.
        return await _viewAccess.HasViewAccessAsync(userId, role, viewId, ct);
    }

    public async Task<int> GetIntervalAsync(CancellationToken ct = default)
    {
        try { return RewindSlots.NormalizeInterval(await _settings.GetAsync<int>(Settings.SettingKeys.Insights.RewindIntervalMinutes, ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return RewindSlots.DefaultIntervalMinutes; }
    }

    public async Task<RewindSeries> GetSeriesAsync(
        Guid viewId, DateTime? endUtc, TimeSpan span, QueueAccessScope scope, CancellationToken ct = default)
    {
        var interval = await GetIntervalAsync(ct);
        var latest = LatestSlot(interval);
        var to = endUtc is { } e && e < latest ? RewindSlots.Floor(e, interval) : latest;
        if (span > IRewindService.MaxSeriesSpan) span = IRewindService.MaxSeriesSpan;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var from = RewindSlots.Floor(to - span, interval);

        var rows = await _store.GetSeriesAsync(viewId, from, to, ct);
        var parsed = rows.Select(r => (
            Row: r,
            Groups: Deserialize<List<RewindGroup>>(r.GroupsJson),
            Counts: VisibleCounts(Deserialize<List<RewindCount>>(r.CountsJson), scope))).ToList();

        var slots = new List<RewindSlot>();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        var idx = 0;
        for (var t = from; t <= to; t = t.AddMinutes(interval))
        {
            // Rows are ordered by captured_utc; advance to the last one at or before t.
            while (idx + 1 < parsed.Count && parsed[idx + 1].Row.CapturedUtc <= t) idx++;
            var hit = parsed.Count > 0 && Covers(parsed[idx].Row.CapturedUtc, parsed[idx].Row.LastSeenUtc, parsed[idx].Row.IntervalMinutes, t)
                ? parsed[idx] : default;
            if (hit.Row is null)
            {
                slots.Add(new RewindSlot(t, false, Empty));
                continue;
            }
            foreach (var key in hit.Counts.Keys) shown.Add(key);
            slots.Add(new RewindSlot(t, true, hit.Counts));
        }

        // Legend: newest layout first (its order is what agents see now),
        // then groups that only existed earlier — visible ones only.
        var groups = new List<RewindGroup>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = parsed.Count - 1; i >= 0; i--)
            foreach (var g in parsed[i].Groups)
                if (shown.Contains(g.Key) && seen.Add(g.Key)) groups.Add(g);

        return new RewindSeries(interval, from, to, latest, groups, slots);
    }

    public async Task<RewindSnapshot> GetSnapshotAsync(
        Guid viewId, DateTime atUtc, QueueAccessScope scope, CancellationToken ct = default)
    {
        var interval = await GetIntervalAsync(ct);
        var latest = LatestSlot(interval);
        var slot = RewindSlots.Floor(atUtc > latest ? latest : atUtc, interval);

        var row = await _store.GetAtAsync(viewId, slot, ct);
        if (row is null || !Covers(row.CapturedUtc, row.LastSeenUtc, row.IntervalMinutes, slot))
            return new RewindSnapshot(slot, false, null, false, Array.Empty<RewindGroup>(), Array.Empty<RewindItem>(), Array.Empty<Guid>());

        var items = Deserialize<List<RewindItem>>(row.ItemsJson).Where(i => scope.CanSee(i.QueueId)).ToList();
        var visibleGroups = items.Select(i => i.Group).ToHashSet(StringComparer.Ordinal);
        var groups = Deserialize<List<RewindGroup>>(row.GroupsJson).Where(g => visibleGroups.Contains(g.Key)).ToList();
        var deleted = await _store.GetDeletedAsync(items.Select(i => i.Id).ToList(), ct);
        return new RewindSnapshot(slot, true, row.CapturedUtc, row.Truncated, groups, items, deleted.ToList());
    }

    /// A row stands for its state from capture until one interval after it
    /// was last confirmed; beyond that nothing was captured (app down).
    internal static bool Covers(DateTime capturedUtc, DateTime lastSeenUtc, int intervalMinutes, DateTime slotUtc)
        => capturedUtc <= slotUtc && slotUtc < lastSeenUtc.AddMinutes(intervalMinutes);

    private static Dictionary<string, int> VisibleCounts(List<RewindCount> counts, QueueAccessScope scope)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in counts)
        {
            if (!scope.CanSee(c.Q)) continue;
            result[c.G] = result.GetValueOrDefault(c.G) + c.N;
        }
        return result;
    }

    private DateTime LatestSlot(int interval) => RewindSlots.Floor(_time.GetUtcNow().UtcDateTime, interval);

    private static readonly IReadOnlyDictionary<string, int> Empty = new Dictionary<string, int>();

    private static T Deserialize<T>(string json) where T : new()
        => JsonSerializer.Deserialize<T>(json, RewindCaptureService.Json) ?? new T();

    private static bool IsAdmin(string role) => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
}
