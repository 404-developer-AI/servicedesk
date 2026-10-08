using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Insights.Rewind;

/// A tracked view as the list would show it right now.
public sealed record RewindArrangedView(IReadOnlyList<RewindArrangedGroup> Groups, bool Truncated);

/// One batch of view layouts sharing one read of the settings (page size,
/// reference prefix, group sort, flag colours) and of taxonomy sort orders.
public interface IRewindLayoutSession
{
    Task<RewindArrangedView> ArrangeAsync(RewindTrackedView view, CancellationToken ct);
}

/// v0.1.31 (shared since v0.1.32) — runs a saved view through the same query
/// (<see cref="RewindViewQuery"/>) and layout (<see cref="RewindArranger"/>) as
/// the ticket list. Used by the Rewind capture and by the Workflow pickup
/// recorder, so both see exactly the list an agent saw.
public interface IRewindLayoutService
{
    Task<IRewindLayoutSession> BeginAsync(CancellationToken ct);
}

public sealed class RewindLayoutService : IRewindLayoutService
{
    private readonly ITicketRepository _tickets;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ISettingsService _settings;

    public RewindLayoutService(ITicketRepository tickets, ITaxonomyRepository taxonomy, ISettingsService settings)
    {
        _tickets = tickets;
        _taxonomy = taxonomy;
        _settings = settings;
    }

    public async Task<IRewindLayoutSession> BeginAsync(CancellationToken ct)
    {
        // Same cap as the list (single-load model): a view shows at most
        // Tickets.ListPageSize rows.
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
        return new Session(this, limit, prefix, groupSort, callbackColor, researchColor);
    }

    private sealed class Session(
        RewindLayoutService owner, int limit, string? prefix, RewindGroupSort groupSort,
        string callbackColor, string researchColor) : IRewindLayoutSession
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, int>> _taxonomyCache = new(StringComparer.Ordinal);

        public async Task<RewindArrangedView> ArrangeAsync(RewindTrackedView view, CancellationToken ct)
        {
            var dc = RewindViewQuery.ParseDisplayConfig(view.DisplayConfigJson);
            IReadOnlyDictionary<string, int>? taxonomy = null;
            if (RewindArranger.IsTaxonomyGroupBy(dc.GroupBy))
            {
                if (!_taxonomyCache.TryGetValue(dc.GroupBy!, out var map))
                {
                    map = await owner.LoadTaxonomySortAsync(dc.GroupBy!, ct);
                    _taxonomyCache[dc.GroupBy!] = map;
                }
                taxonomy = map;
            }

            var query = RewindViewQuery.Build(view.FiltersJson, dc, limit, prefix);
            var page = await owner._tickets.SearchAsync(query, VisibilityScope.All, null, null, ct);
            var truncated = page.NextOffset is not null || page.NextCursorId is not null;
            var groups = RewindArranger.Arrange(
                page.Items, dc, new RewindLayoutContext(groupSort, callbackColor, researchColor, taxonomy));
            return new RewindArrangedView(groups, truncated);
        }
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
