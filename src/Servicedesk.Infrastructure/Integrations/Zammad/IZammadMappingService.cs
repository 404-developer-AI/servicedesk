namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// One Zammad object ↔ servicedesk taxonomy row mapping, as surfaced to
/// the integration page. <see cref="MappedToId"/> is null when the admin
/// has not picked a target yet — the UI renders the row with a
/// "needs mapping" pill so the missing rows stand out before the admin
/// even clicks Dry-run.
public sealed record ZammadMappingRow(
    long ZammadId,
    string ZammadName,
    bool ZammadActive,
    Guid? MappedToId,
    string? MappedToName);

/// One local taxonomy choice — queue / status / priority — projected to
/// the {id, name} pair the UI dropdown needs.
public sealed record ZammadMappingTarget(Guid Id, string Name, bool IsActive);

/// Top-level overview the SPA's Mapping section consumes in one call.
/// Combines (a) live Zammad lists fetched through <see cref="IZammadApiClient"/>,
/// (b) local taxonomy rows from <c>queues</c> / <c>statuses</c> /
/// <c>priorities</c>, and (c) the persisted mapping rows joined against
/// both. <see cref="UnmappedGroupCount"/> / <see cref="UnmappedStateCount"/>
/// / <see cref="UnmappedPriorityCount"/> let the SPA flip the Dry-run
/// button between enabled and disabled without re-walking the lists.
public sealed record ZammadMappingOverview(
    IReadOnlyList<ZammadMappingRow> Groups,
    IReadOnlyList<ZammadMappingRow> States,
    IReadOnlyList<ZammadMappingRow> Priorities,
    IReadOnlyList<ZammadMappingTarget> LocalQueues,
    IReadOnlyList<ZammadMappingTarget> LocalStatuses,
    IReadOnlyList<ZammadMappingTarget> LocalPriorities,
    int UnmappedGroupCount,
    int UnmappedStateCount,
    int UnmappedPriorityCount);

/// In-memory dictionaries fetched in one DB hit at the start of a dry-run.
/// Keyed by Zammad id — the resolver hot-path only needs O(1) lookups.
public sealed record ZammadMappingDictionary(
    IReadOnlyDictionary<long, Guid> GroupToQueue,
    IReadOnlyDictionary<long, Guid> StateToStatus,
    IReadOnlyDictionary<long, Guid> PriorityToPriority);

/// Combined CRUD + loader surface for the v0.0.41 mapping tables. The
/// admin endpoint uses GetOverview + the Upsert/Delete methods; the
/// dry-run worker calls <see cref="LoadDictionaryAsync"/> once at run-
/// start so the per-ticket resolver doesn't hit the DB.
public interface IZammadMappingService
{
    Task<ZammadMappingOverview> GetOverviewAsync(CancellationToken ct);

    Task<ZammadMappingDictionary> LoadDictionaryAsync(CancellationToken ct);

    Task UpsertGroupMappingAsync(long zammadGroupId, string zammadGroupName, Guid queueId, CancellationToken ct);
    Task<bool> DeleteGroupMappingAsync(long zammadGroupId, CancellationToken ct);

    Task UpsertStateMappingAsync(long zammadStateId, string zammadStateName, Guid statusId, CancellationToken ct);
    Task<bool> DeleteStateMappingAsync(long zammadStateId, CancellationToken ct);

    Task UpsertPriorityMappingAsync(long zammadPriorityId, string zammadPriorityName, Guid priorityId, CancellationToken ct);
    Task<bool> DeletePriorityMappingAsync(long zammadPriorityId, CancellationToken ct);
}
