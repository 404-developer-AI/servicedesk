using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Insights.Rewind;

public interface IRewindCaptureService
{
    /// Captures every tracked view for <paramref name="slotUtc"/>. Returns
    /// the number of views that produced a new (changed) snapshot.
    Task<int> CaptureAsync(DateTime slotUtc, int intervalMinutes, CancellationToken ct);
}

/// One capture = one list query per tracked view, run through the same
/// query builder and layout as the ticket list (IRewindLayoutService), then stored only when the
/// result differs from the view's previous snapshot. A view that fails is
/// logged and skipped; the others still capture.
public sealed class RewindCaptureService : IRewindCaptureService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IRewindStore _store;
    private readonly IRewindLayoutService _layout;
    private readonly ILogger<RewindCaptureService> _logger;

    public RewindCaptureService(IRewindStore store, IRewindLayoutService layout, ILogger<RewindCaptureService> logger)
    {
        _store = store;
        _layout = layout;
        _logger = logger;
    }

    public async Task<int> CaptureAsync(DateTime slotUtc, int intervalMinutes, CancellationToken ct)
    {
        var views = await _store.ListTrackedViewsAsync(ct);
        if (views.Count == 0) return 0;

        var session = await _layout.BeginAsync(ct);
        var changed = 0;
        foreach (var view in views)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var arranged = await session.ArrangeAsync(view, ct);
                var capture = BuildCapture(view.Id, slotUtc, intervalMinutes, arranged.Groups, arranged.Truncated);
                var latest = await _store.GetLatestAsync(view.Id, ct);
                // A changed interval starts a new row: coverage is computed from the
                // row's own interval, so one row must never mix two cadences.
                if (latest is not null && latest.ContentHash == capture.ContentHash
                    && latest.IntervalMinutes == intervalMinutes && latest.CapturedUtc <= slotUtc)
                {
                    await _store.TouchAsync(latest.Id, slotUtc, ct);
                }
                else
                {
                    if (latest is not null)
                        capture = await WithChangesAsync(capture, latest, ct);
                    await _store.InsertAsync(capture, ct);
                    changed++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rewind capture failed for view {ViewId}; skipped this slot.", view.Id);
            }
        }
        return changed;
    }

    /// Diffs a changed capture against the view's previous row: arrivals,
    /// and leavers with the reason they left (looked up in the ticket's
    /// events since the previous row was captured).
    private async Task<RewindCapture> WithChangesAsync(RewindCapture capture, RewindLatest previous, CancellationToken ct)
    {
        var previousJson = await _store.GetItemsAsync(previous.Id, ct);
        if (previousJson is null) return capture;
        var before = JsonSerializer.Deserialize<List<RewindItem>>(previousJson, Json) ?? new();
        var after = JsonSerializer.Deserialize<List<RewindItem>>(capture.ItemsJson, Json) ?? new();
        var afterIds = after.Select(i => i.Id).ToHashSet();
        var leaverIds = before.Where(i => !afterIds.Contains(i.Id)).Select(i => i.Id).ToList();
        var reasons = await _store.ClassifyLeaversAsync(leaverIds, previous.CapturedUtc, ct);
        var (added, removed) = ComputeChanges(before, after, reasons);
        return capture with
        {
            AddedJson = JsonSerializer.Serialize(added, Json),
            RemovedJson = JsonSerializer.Serialize(removed, Json),
        };
    }

    internal static (IReadOnlyList<RewindChange> Added, IReadOnlyList<RewindChange> Removed) ComputeChanges(
        IReadOnlyList<RewindItem> before, IReadOnlyList<RewindItem> after, IReadOnlyDictionary<Guid, string> reasons)
    {
        var beforeIds = before.Select(i => i.Id).ToHashSet();
        var afterIds = after.Select(i => i.Id).ToHashSet();
        var added = after.Where(i => !beforeIds.Contains(i.Id))
            .Select(i => new RewindChange(i.Id, i.QueueId, i.Number, i.Subject)).ToList();
        var removed = before.Where(i => !afterIds.Contains(i.Id))
            .Select(i => new RewindChange(i.Id, i.QueueId, i.Number, i.Subject,
                reasons.TryGetValue(i.Id, out var r) ? r : RewindLeaveReason.Other))
            .ToList();
        return (added, removed);
    }

    internal static RewindCapture BuildCapture(
        Guid viewId, DateTime slotUtc, int intervalMinutes,
        IReadOnlyList<RewindArrangedGroup> groups, bool truncated)
    {
        var groupHeaders = new List<RewindGroup>(groups.Count);
        var items = new List<RewindItem>();
        var counts = new List<RewindCount>();
        foreach (var g in groups)
        {
            groupHeaders.Add(new RewindGroup(g.Key, g.Label, g.Color));
            foreach (var byQueue in g.Items.GroupBy(t => t.QueueId))
                counts.Add(new RewindCount(g.Key, byQueue.Key, byQueue.Count()));
            foreach (var t in g.Items)
                items.Add(ToItem(t, g.Key));
        }

        var groupsJson = JsonSerializer.Serialize(groupHeaders, Json);
        var itemsJson = JsonSerializer.Serialize(items, Json);
        var countsJson = JsonSerializer.Serialize(counts, Json);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{(truncated ? 1 : 0)}\n{groupsJson}\n{itemsJson}")));
        return new RewindCapture(viewId, slotUtc, intervalMinutes, items.Count, truncated,
            hash, groupsJson, countsJson, itemsJson);
    }

    private static RewindItem ToItem(TicketListItem t, string group)
    {
        var name = $"{t.RequesterFirstName} {t.RequesterLastName}".Trim();
        return new RewindItem(
            t.Id, t.Number, t.Subject, t.QueueId, t.QueueName,
            t.StatusId, t.StatusName, t.StatusColor, t.StatusStateCategory,
            t.PriorityName, t.PriorityColor, t.PriorityIsDefault,
            t.IsCallback, t.IsResearch,
            name.Length > 0 ? name : t.RequesterEmail,
            t.CompanyName, t.AssigneeUserId, t.AssigneeEmail,
            t.CreatedUtc, t.PendingTillUtc, group);
    }
}
