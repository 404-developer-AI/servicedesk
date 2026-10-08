using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Insights.Rewind;

public interface IRewindCaptureService
{
    /// Captures every tracked view for <paramref name="slotUtc"/>. Returns
    /// the number of views that produced a new (changed) snapshot.
    Task<int> CaptureAsync(DateTime slotUtc, int intervalMinutes, CancellationToken ct);
}

/// One capture = one list query per tracked view, run through the same
/// query builder and layout as the ticket list, then stored only when the
/// result differs from the view's previous snapshot. A view that fails is
/// logged and skipped; the others still capture.
public sealed class RewindCaptureService : IRewindCaptureService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IRewindStore _store;
    private readonly ITicketRepository _tickets;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ISettingsService _settings;
    private readonly ILogger<RewindCaptureService> _logger;

    public RewindCaptureService(
        IRewindStore store, ITicketRepository tickets, ITaxonomyRepository taxonomy,
        ISettingsService settings, ILogger<RewindCaptureService> logger)
    {
        _store = store;
        _tickets = tickets;
        _taxonomy = taxonomy;
        _settings = settings;
        _logger = logger;
    }

    public async Task<int> CaptureAsync(DateTime slotUtc, int intervalMinutes, CancellationToken ct)
    {
        var views = await _store.ListTrackedViewsAsync(ct);
        if (views.Count == 0) return 0;

        // Same cap as the list (single-load model): a view shows at most
        // Tickets.ListPageSize rows, so that is what it snapshots.
        var limit = await GetOrAsync(SettingKeys.Tickets.ListPageSize, 1000, ct);
        if (limit < 1) limit = 1000;
        var prefix = await GetOrAsync<string?>(SettingKeys.Tickets.ReferencePrefix, TicketReference.DefaultPrefix, ct);
        var groupSort = new RewindGroupSort(
            await GetOrAsync(SettingKeys.Tickets.GroupSortEnabled, false, ct),
            await GetOrAsync(SettingKeys.Tickets.GroupSortPendingField, "pendingTillUtc", ct) ?? "pendingTillUtc",
            await GetOrAsync(SettingKeys.Tickets.GroupSortPendingDirection, "asc", ct) ?? "asc",
            await GetOrAsync(SettingKeys.Tickets.GroupSortOpenNewField, "updatedUtc", ct) ?? "updatedUtc",
            await GetOrAsync(SettingKeys.Tickets.GroupSortOpenNewDirection, "asc", ct) ?? "asc");
        var callbackColor = await GetOrAsync(SettingKeys.Tickets.CallbackColor, "#22c55e", ct) ?? "#22c55e";
        var researchColor = await GetOrAsync(SettingKeys.Tickets.ResearchColor, "#3b82f6", ct) ?? "#3b82f6";

        var taxonomyCache = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        var changed = 0;
        foreach (var view in views)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var dc = RewindViewQuery.ParseDisplayConfig(view.DisplayConfigJson);
                IReadOnlyDictionary<string, int>? taxonomy = null;
                if (RewindArranger.IsTaxonomyGroupBy(dc.GroupBy))
                {
                    if (!taxonomyCache.TryGetValue(dc.GroupBy!, out var map))
                    {
                        map = await LoadTaxonomySortAsync(dc.GroupBy!, ct);
                        taxonomyCache[dc.GroupBy!] = map;
                    }
                    taxonomy = map;
                }

                var query = RewindViewQuery.Build(view.FiltersJson, dc, limit, prefix);
                var page = await _tickets.SearchAsync(query, VisibilityScope.All, null, null, ct);
                var truncated = page.NextOffset is not null || page.NextCursorId is not null;
                var groups = RewindArranger.Arrange(
                    page.Items, dc, new RewindLayoutContext(groupSort, callbackColor, researchColor, taxonomy));

                var capture = BuildCapture(view.Id, slotUtc, intervalMinutes, groups, truncated);
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

    private async Task<IReadOnlyDictionary<string, int>> LoadTaxonomySortAsync(string groupBy, CancellationToken ct)
    {
        IEnumerable<(Guid Id, int SortOrder)> rows = groupBy switch
        {
            "statusId" => (await _taxonomy.ListStatusesAsync(ct)).Select(s => (s.Id, s.SortOrder)),
            "priorityId" => (await _taxonomy.ListPrioritiesAsync(ct)).Select(p => (p.Id, p.SortOrder)),
            "queueId" => (await _taxonomy.ListQueuesAsync(ct)).Select(q => (q.Id, q.SortOrder)),
            "categoryId" => (await _taxonomy.ListCategoriesAsync(ct)).Select(c => (c.Id, c.SortOrder)),
            _ => Array.Empty<(Guid, int)>(),
        };
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, sortOrder) in rows) map[id.ToString()] = sortOrder;
        return map;
    }

    private async Task<T> GetOrAsync<T>(string key, T fallback, CancellationToken ct)
    {
        try { return await _settings.GetAsync<T>(key, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return fallback; }
    }
}
