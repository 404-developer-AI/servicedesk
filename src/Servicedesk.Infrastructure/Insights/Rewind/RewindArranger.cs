using System.Globalization;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Insights.Rewind;

/// Per-state sort inside status groups (Settings → Tickets → Grouping),
/// the same values <c>GET /api/settings/ticket-grouping</c> serves the list.
public sealed record RewindGroupSort(
    bool Enabled, string PendingField, string PendingDirection, string OpenNewField, string OpenNewDirection)
{
    public static readonly RewindGroupSort Off = new(false, "pendingTillUtc", "asc", "updatedUtc", "asc");
}

/// Everything besides the rows that decides how a view is laid out.
/// <see cref="TaxonomySortOrder"/> holds the sort order of the entity the
/// view groups by (status / priority / queue / category), keyed by id;
/// null for the other group-by fields.
public sealed record RewindLayoutContext(
    RewindGroupSort GroupSort,
    string CallbackColor,
    string ResearchColor,
    IReadOnlyDictionary<string, int>? TaxonomySortOrder);

public sealed record RewindArrangedGroup(string Key, string Label, string? Color, IReadOnlyList<TicketListItem> Items);

/// Server port of <c>GroupedTicketList</c>'s layout (float buckets, group-by
/// field, group order and the per-state group sort), applied to the rows in
/// the order the list query returned them. It must stay in lockstep with
/// the client component — like the float order already stays in lockstep
/// with <c>lib/ticketFlags.ts</c>. A snapshot freezes the result, so a later
/// change to the view or the grouping settings never rewrites history.
public static class RewindArranger
{
    public const string PriorityFloatColor = "#ef4444";

    public const string KeyAll = "__all__";
    public const string KeyPriority = "__float__";
    public const string KeyCallback = "__float_callback__";
    public const string KeyResearch = "__float_research__";
    private const string KeyNull = "__null__";

    /// Group-by fields the list supports (GROUP_BY_FIELD_MAP). Anything else
    /// is "no grouping", as on the client.
    private static readonly HashSet<string> GroupByFields = new(StringComparer.Ordinal)
    {
        "statusId", "priorityId", "queueId", "assigneeUserId", "categoryId", "requesterContactId", "companyName",
    };

    public static bool IsTaxonomyGroupBy(string? groupBy)
        => groupBy is "statusId" or "priorityId" or "queueId" or "categoryId";

    public static IReadOnlyList<RewindArrangedGroup> Arrange(
        IReadOnlyList<TicketListItem> items, RewindDisplayConfig dc, RewindLayoutContext ctx)
    {
        var groupBy = dc.GroupBy is { } g && GroupByFields.Contains(g) ? g : null;
        if (groupBy is null && !dc.HasAnyFloat)
            return new[] { new RewindArrangedGroup(KeyAll, "", null, items) };

        var floats = new[] { new List<TicketListItem>(), new List<TicketListItem>(), new List<TicketListItem>() };
        var normal = new List<TicketListItem>();
        foreach (var t in items)
        {
            var b = dc.HasAnyFloat ? FloatBucket(t, dc) : null;
            if (b is null) normal.Add(t);
            else floats[b.Value].Add(t);
        }

        var result = new List<RewindArrangedGroup>();
        if (floats[0].Count > 0) result.Add(new(KeyPriority, "Priority", PriorityFloatColor, floats[0]));
        if (floats[1].Count > 0) result.Add(new(KeyCallback, "Call-back", ctx.CallbackColor, floats[1]));
        if (floats[2].Count > 0) result.Add(new(KeyResearch, "Research", ctx.ResearchColor, floats[2]));

        if (groupBy is not null)
        {
            var ordered = OrderGroups(GroupBy(normal, groupBy), dc.GroupOrder, ctx.TaxonomySortOrder);
            if (groupBy == "statusId" && ctx.GroupSort.Enabled)
                ordered = ordered.Select(grp => ApplyGroupSort(grp, ctx.GroupSort)).ToList();
            result.AddRange(ordered);
        }
        else if (normal.Count > 0)
        {
            result.Add(new(KeyAll, "All tickets", null, normal));
        }
        return result;
    }

    /// 0 = Priority, 1 = Call-back, 2 = Research, null = normal list. Same
    /// rule as <c>floatBucket</c> in lib/ticketFlags.ts.
    public static int? FloatBucket(TicketListItem t, RewindDisplayConfig dc)
    {
        if (t.StatusStateCategory is not ("New" or "Open")) return null;
        if (dc.PriorityFloat && !t.PriorityIsDefault) return 0;
        if (dc.CallbackFloat && t.IsCallback) return 1;
        if (dc.ResearchFloat && t.IsResearch) return 2;
        return null;
    }

    private static List<RewindArrangedGroup> GroupBy(List<TicketListItem> items, string field)
    {
        var groups = new List<(string Key, string Label, string? Color, List<TicketListItem> Items)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in items)
        {
            var (id, label, color) = field switch
            {
                "statusId" => (Id(t.StatusId), t.StatusName, t.StatusColor),
                "priorityId" => (Id(t.PriorityId), t.PriorityName, t.PriorityColor),
                "queueId" => (Id(t.QueueId), t.QueueName, (string?)null),
                "assigneeUserId" => (Id(t.AssigneeUserId), t.AssigneeEmail, null),
                "categoryId" => (Id(t.CategoryId), t.CategoryName, null),
                "requesterContactId" => (Id(t.RequesterContactId), t.RequesterEmail, null),
                _ => (t.CompanyName ?? KeyNull, t.CompanyName, null),
            };
            if (!index.TryGetValue(id, out var i))
            {
                i = groups.Count;
                index[id] = i;
                groups.Add((id, label ?? "Unassigned", string.IsNullOrEmpty(color) ? null : color, new List<TicketListItem>()));
            }
            groups[i].Items.Add(t);
        }
        return groups.Select(x => new RewindArrangedGroup(x.Key, x.Label, x.Color, x.Items)).ToList();
    }

    // JS String(uuid) and Guid.ToString() agree: lowercase "D" format.
    private static string Id(Guid id) => id.ToString();
    private static string Id(Guid? id) => id?.ToString() ?? KeyNull;

    private static List<RewindArrangedGroup> OrderGroups(
        List<RewindArrangedGroup> groups, IReadOnlyList<string> groupOrder, IReadOnlyDictionary<string, int>? taxonomy)
    {
        int Tax(RewindArrangedGroup grp) => taxonomy is not null && taxonomy.TryGetValue(grp.Key, out var v) ? v : 99999;

        // List.Sort is unstable; OrderBy/ThenBy is stable like Array.prototype.sort.
        if (groupOrder.Count > 0)
        {
            var orderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < groupOrder.Count; i++) orderIndex.TryAdd(groupOrder[i], i);
            return groups
                .OrderBy(grp => orderIndex.TryGetValue(grp.Key, out var i) ? i : 99999)
                .ThenBy(Tax)
                .ToList();
        }
        if (taxonomy is not null)
            return groups.OrderBy(Tax).ThenBy(grp => grp.Label, LabelComparer).ToList();
        return groups.OrderBy(grp => grp.Label, LabelComparer).ToList();
    }

    private static readonly StringComparer LabelComparer = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: false);

    private static RewindArrangedGroup ApplyGroupSort(RewindArrangedGroup grp, RewindGroupSort cfg)
    {
        var category = grp.Items.Count > 0 ? grp.Items[0].StatusStateCategory : null;
        return category switch
        {
            "Pending" => grp with { Items = SortItems(grp.Items, cfg.PendingField, cfg.PendingDirection) },
            "New" or "Open" => grp with { Items = SortItems(grp.Items, cfg.OpenNewField, cfg.OpenNewDirection) },
            _ => grp,
        };
    }

    /// Same accessors as GROUP_SORT_ACCESSORS; a missing value sorts last in
    /// either direction; an unknown field keeps the incoming order.
    private static IReadOnlyList<TicketListItem> SortItems(IReadOnlyList<TicketListItem> items, string field, string direction)
    {
        Func<TicketListItem, double?>? accessor = field switch
        {
            "updatedUtc" => t => t.UpdatedUtc.Ticks,
            "createdUtc" => t => t.CreatedUtc.Ticks,
            "pendingTillUtc" => t => t.PendingTillUtc?.Ticks,
            "dueUtc" => t => t.DueUtc?.Ticks,
            "priorityLevel" => t => t.PriorityLevel,
            "number" => t => t.Number,
            _ => null,
        };
        if (accessor is null) return items;
        var desc = direction == "desc";
        var keyed = items.Select(t => (Item: t, Value: accessor(t))).ToList();
        var present = keyed.Where(x => x.Value.HasValue);
        var sorted = desc
            ? present.OrderByDescending(x => x.Value!.Value)
            : present.OrderBy(x => x.Value!.Value);
        return sorted.Concat(keyed.Where(x => !x.Value.HasValue)).Select(x => x.Item).ToList();
    }
}
