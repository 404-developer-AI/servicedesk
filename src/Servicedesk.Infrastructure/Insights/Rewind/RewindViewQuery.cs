using System.Text.Json;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Insights.Rewind;

/// The parts of a view's display config that decide the order and grouping
/// of its list. Mirrors the client <c>DisplayConfig</c> type; the search-box
/// keys are irrelevant to a snapshot and are not read.
public sealed record RewindDisplayConfig(
    bool PriorityFloat,
    bool CallbackFloat,
    bool ResearchFloat,
    bool StateBucketSort,
    string? GroupBy,
    IReadOnlyList<string> GroupOrder,
    string? SortField,
    string? SortDirection)
{
    public static readonly RewindDisplayConfig Empty =
        new(false, false, false, false, null, Array.Empty<string>(), null, null);

    public bool HasAnyFloat => PriorityFloat || CallbackFloat || ResearchFloat;
}

/// Turns a saved view (filters JSON + display config JSON) into the same
/// <see cref="TicketQuery"/> the ticket list sends for it. Kept in lockstep
/// with <c>TicketListPage</c> (filter parsing) and the <c>GET /api/tickets</c>
/// handler (parameter mapping): a snapshot must hold exactly the rows, in
/// exactly the order, an agent saw in that view.
///
/// The query is deliberately unscoped (no AccessibleQueueIds): the capture
/// runs as the system, and queue access is applied when a snapshot is read.
public static class RewindViewQuery
{
    public static RewindDisplayConfig ParseDisplayConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return RewindDisplayConfig.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return RewindDisplayConfig.Empty;

            string? sortField = null, sortDirection = null;
            if (root.TryGetProperty("sort", out var sort) && sort.ValueKind == JsonValueKind.Object)
            {
                sortField = ReadString(sort, "field");
                sortDirection = ReadString(sort, "direction");
            }

            var groupOrder = new List<string>();
            if (root.TryGetProperty("groupOrder", out var order) && order.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in order.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) groupOrder.Add(s);
            }

            return new RewindDisplayConfig(
                PriorityFloat: ReadTrue(root, "priorityFloat"),
                CallbackFloat: ReadTrue(root, "callbackFloat"),
                ResearchFloat: ReadTrue(root, "researchFloat"),
                StateBucketSort: ReadTrue(root, "stateBucketSort"),
                GroupBy: ReadString(root, "groupBy"),
                GroupOrder: groupOrder,
                SortField: sortField,
                SortDirection: sortDirection);
        }
        catch (JsonException)
        {
            // Same as the list: a bad display config shows the plain list.
            return RewindDisplayConfig.Empty;
        }
    }

    /// <paramref name="referencePrefix"/> is <c>Tickets.ReferencePrefix</c>,
    /// used to normalise a pasted ticket reference in the view's search
    /// filter exactly like the list endpoint does.
    public static TicketQuery Build(
        string? filtersJson, RewindDisplayConfig dc, int limit, string? referencePrefix)
    {
        IReadOnlyList<Guid>? queueIds = null, statusIds = null, priorityIds = null;
        Guid? assignee = null;
        string? search = null;
        bool openOnly = false, projectsOnly = false, callbacksOnly = false, researchOnly = false;

        if (!string.IsNullOrWhiteSpace(filtersJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(filtersJson);
                var vf = doc.RootElement;
                if (vf.ValueKind == JsonValueKind.Object)
                {
                    queueIds = ReadIdList(vf, "queueIds", "queueId");
                    statusIds = ReadIdList(vf, "statusIds", "statusId");
                    priorityIds = ReadIdList(vf, "priorityIds", "priorityId");
                    if (ReadString(vf, "assigneeUserId") is { } a && Guid.TryParse(a, out var ag)) assignee = ag;
                    if (vf.TryGetProperty("search", out var s) && s.ValueKind == JsonValueKind.String)
                        search = s.GetString();
                    openOnly = ReadTrue(vf, "openOnly");
                    projectsOnly = ReadTrue(vf, "projectsOnly");
                    callbacksOnly = ReadTrue(vf, "callbacksOnly");
                    researchOnly = ReadTrue(vf, "researchOnly");
                }
            }
            catch (JsonException)
            {
                // The list ignores bad filter JSON and shows everything; so do we.
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
            search = TicketReference.NormalizeSearchTerm(search, referencePrefix);

        return new TicketQuery(
            AssigneeUserId: assignee,
            Search: search,
            OpenOnly: openOnly,
            StateBucketSort: dc.StateBucketSort,
            SortField: dc.SortField,
            SortDirection: dc.SortDirection,
            PriorityFloat: dc.PriorityFloat,
            Limit: limit,
            QueueIds: queueIds,
            StatusIds: statusIds,
            PriorityIds: priorityIds,
            ProjectsOnly: projectsOnly,
            CallbackFloat: dc.CallbackFloat,
            ResearchFloat: dc.ResearchFloat,
            CallbacksOnly: callbacksOnly,
            ResearchOnly: researchOnly);
    }

    /// Multi-select array wins; the legacy singular key folds into a
    /// one-element list. Invalid ids are dropped (the list endpoint's
    /// ParseGuidList does the same with the comma-joined form).
    private static IReadOnlyList<Guid>? ReadIdList(JsonElement vf, string listKey, string singularKey)
    {
        if (vf.TryGetProperty(listKey, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var ids = new List<Guid>();
            foreach (var e in list.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out var g)) ids.Add(g);
            if (ids.Count > 0) return ids;
        }
        if (ReadString(vf, singularKey) is { } single && Guid.TryParse(single, out var one))
            return new[] { one };
        return null;
    }

    private static bool ReadTrue(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s : null;
}
