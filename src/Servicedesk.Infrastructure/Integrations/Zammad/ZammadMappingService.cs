using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Dapper-backed implementation of <see cref="IZammadMappingService"/>.
/// Two responsibilities:
/// <list type="bullet">
/// <item>Drive the integration-page Mapping section. <see cref="GetOverviewAsync"/>
/// joins live Zammad lists (groups / states / priorities, via
/// <see cref="IZammadApiClient"/>) with the local taxonomy
/// (<see cref="ITaxonomyRepository"/>) and the persisted mapping rows
/// in one call so the SPA never has to fan-out three roundtrips.</item>
/// <item>Pre-load the resolver dictionary for the dry-run worker.
/// <see cref="LoadDictionaryAsync"/> reads all three mapping tables in
/// one query and returns O(1) dictionaries — the resolver hot path on
/// a 10K-ticket dry-run does no DB I/O per ticket beyond the contact
/// lookup.</item>
/// </list>
/// All SELECT columns are aliased to PascalCase per the project Dapper
/// convention. Upsert/delete write paths emit audit_log security rows
/// through the endpoint layer (the service stays Postgres-only so a
/// background worker can reuse it without dragging the audit logger
/// scope in).
public sealed class ZammadMappingService : IZammadMappingService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IZammadApiClient _zammad;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ILogger<ZammadMappingService> _logger;

    public ZammadMappingService(
        NpgsqlDataSource dataSource,
        IZammadApiClient zammad,
        ITaxonomyRepository taxonomy,
        ILogger<ZammadMappingService> logger)
    {
        _dataSource = dataSource;
        _zammad = zammad;
        _taxonomy = taxonomy;
        _logger = logger;
    }

    public async Task<ZammadMappingOverview> GetOverviewAsync(CancellationToken ct)
    {
        // Fan out the four reads in parallel — Zammad upstream + Postgres
        // are independent. WhenAll propagates the first exception so the
        // endpoint can wrap a Zammad-side failure in a 502 envelope while
        // the local taxonomy reads continue (cheap enough to discard if
        // the upstream fails).
        var groupsTask = _zammad.ListGroupsAsync(ct);
        var statesTask = _zammad.ListStatesAsync(ct);
        var prioritiesTask = _zammad.ListPrioritiesAsync(ct);
        var queuesTask = _taxonomy.ListQueuesAsync(ct);
        var statusesTask = _taxonomy.ListStatusesAsync(ct);
        var localPrioritiesTask = _taxonomy.ListPrioritiesAsync(ct);
        var mappingsTask = LoadAllMappingRowsAsync(ct);

        await Task.WhenAll(
            groupsTask, statesTask, prioritiesTask,
            queuesTask, statusesTask, localPrioritiesTask,
            mappingsTask);

        var zammadGroups = await groupsTask;
        var zammadStates = await statesTask;
        var zammadPriorities = await prioritiesTask;
        var queues = await queuesTask;
        var statuses = await statusesTask;
        var localPriorities = await localPrioritiesTask;
        var mappings = await mappingsTask;

        var queueById = queues.ToDictionary(q => q.Id, q => q.Name);
        var statusById = statuses.ToDictionary(s => s.Id, s => s.Name);
        var localPriorityById = localPriorities.ToDictionary(p => p.Id, p => p.Name);

        // For each Zammad row, find the mapping (if any) and project to
        // ZammadMappingRow. Unmapped rows come back with null target —
        // the UI surfaces them with a "needs mapping" pill.
        var groupRows = zammadGroups.Select(g =>
        {
            var mapped = mappings.GroupRows.TryGetValue(g.Id, out var m) ? (Guid?)m : null;
            var mappedName = mapped.HasValue && queueById.TryGetValue(mapped.Value, out var n) ? n : null;
            return new ZammadMappingRow(g.Id, g.Name, g.Active, mapped, mappedName);
        }).ToList();

        var stateRows = zammadStates.Select(s =>
        {
            var mapped = mappings.StateRows.TryGetValue(s.Id, out var m) ? (Guid?)m : null;
            var mappedName = mapped.HasValue && statusById.TryGetValue(mapped.Value, out var n) ? n : null;
            return new ZammadMappingRow(s.Id, s.Name, s.Active, mapped, mappedName);
        }).ToList();

        var priorityRows = zammadPriorities.Select(p =>
        {
            var mapped = mappings.PriorityRows.TryGetValue(p.Id, out var m) ? (Guid?)m : null;
            var mappedName = mapped.HasValue && localPriorityById.TryGetValue(mapped.Value, out var n) ? n : null;
            return new ZammadMappingRow(p.Id, p.Name, p.Active, mapped, mappedName);
        }).ToList();

        var localQueueTargets = queues
            .Select(q => new ZammadMappingTarget(q.Id, q.Name, q.IsActive))
            .ToList();
        var localStatusTargets = statuses
            .Select(s => new ZammadMappingTarget(s.Id, s.Name, s.IsActive))
            .ToList();
        var localPriorityTargets = localPriorities
            .Select(p => new ZammadMappingTarget(p.Id, p.Name, p.IsActive))
            .ToList();

        return new ZammadMappingOverview(
            Groups: groupRows,
            States: stateRows,
            Priorities: priorityRows,
            LocalQueues: localQueueTargets,
            LocalStatuses: localStatusTargets,
            LocalPriorities: localPriorityTargets,
            UnmappedGroupCount: groupRows.Count(r => r.MappedToId is null),
            UnmappedStateCount: stateRows.Count(r => r.MappedToId is null),
            UnmappedPriorityCount: priorityRows.Count(r => r.MappedToId is null));
    }

    public async Task<ZammadMappingDictionary> LoadDictionaryAsync(CancellationToken ct)
    {
        var mappings = await LoadAllMappingRowsAsync(ct);
        return new ZammadMappingDictionary(
            GroupToQueue: mappings.GroupRows,
            StateToStatus: mappings.StateRows,
            PriorityToPriority: mappings.PriorityRows);
    }

    public async Task UpsertGroupMappingAsync(long zammadGroupId, string zammadGroupName, Guid queueId, CancellationToken ct)
    {
        // ON CONFLICT on the natural key (zammad_group_id) — the admin
        // re-maps the same Zammad group by overwriting the queue_id, not
        // by creating a new row.
        const string sql = """
            INSERT INTO zammad_group_mappings (
                zammad_group_id, zammad_group_name, queue_id, created_utc, updated_utc)
            VALUES (@ZammadGroupId, @ZammadGroupName, @QueueId, now(), now())
            ON CONFLICT (zammad_group_id) DO UPDATE SET
                zammad_group_name = EXCLUDED.zammad_group_name,
                queue_id          = EXCLUDED.queue_id,
                updated_utc       = now()
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            ZammadGroupId = zammadGroupId,
            ZammadGroupName = zammadGroupName,
            QueueId = queueId,
        }, cancellationToken: ct));
    }

    public async Task<bool> DeleteGroupMappingAsync(long zammadGroupId, CancellationToken ct)
    {
        const string sql = "DELETE FROM zammad_group_mappings WHERE zammad_group_id = @ZammadGroupId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { ZammadGroupId = zammadGroupId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task UpsertStateMappingAsync(long zammadStateId, string zammadStateName, Guid statusId, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO zammad_state_mappings (
                zammad_state_id, zammad_state_name, status_id, created_utc, updated_utc)
            VALUES (@ZammadStateId, @ZammadStateName, @StatusId, now(), now())
            ON CONFLICT (zammad_state_id) DO UPDATE SET
                zammad_state_name = EXCLUDED.zammad_state_name,
                status_id         = EXCLUDED.status_id,
                updated_utc       = now()
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            ZammadStateId = zammadStateId,
            ZammadStateName = zammadStateName,
            StatusId = statusId,
        }, cancellationToken: ct));
    }

    public async Task<bool> DeleteStateMappingAsync(long zammadStateId, CancellationToken ct)
    {
        const string sql = "DELETE FROM zammad_state_mappings WHERE zammad_state_id = @ZammadStateId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { ZammadStateId = zammadStateId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task UpsertPriorityMappingAsync(long zammadPriorityId, string zammadPriorityName, Guid priorityId, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO zammad_priority_mappings (
                zammad_priority_id, zammad_priority_name, priority_id, created_utc, updated_utc)
            VALUES (@ZammadPriorityId, @ZammadPriorityName, @PriorityId, now(), now())
            ON CONFLICT (zammad_priority_id) DO UPDATE SET
                zammad_priority_name = EXCLUDED.zammad_priority_name,
                priority_id          = EXCLUDED.priority_id,
                updated_utc          = now()
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            ZammadPriorityId = zammadPriorityId,
            ZammadPriorityName = zammadPriorityName,
            PriorityId = priorityId,
        }, cancellationToken: ct));
    }

    public async Task<bool> DeletePriorityMappingAsync(long zammadPriorityId, CancellationToken ct)
    {
        const string sql = "DELETE FROM zammad_priority_mappings WHERE zammad_priority_id = @ZammadPriorityId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { ZammadPriorityId = zammadPriorityId }, cancellationToken: ct));
        return n > 0;
    }

    // ---- private helpers ----------------------------------------------

    /// One-shot read of all three mapping tables, materialised into
    /// dictionaries keyed by Zammad id. Three small queries are cheaper
    /// than three UNIONs across heterogeneous shapes.
    private async Task<MappingRowsBundle> LoadAllMappingRowsAsync(CancellationToken ct)
    {
        const string groupSql = """
            SELECT zammad_group_id AS "ZammadId", queue_id AS "TargetId"
              FROM zammad_group_mappings
            """;
        const string stateSql = """
            SELECT zammad_state_id AS "ZammadId", status_id AS "TargetId"
              FROM zammad_state_mappings
            """;
        const string prioritySql = """
            SELECT zammad_priority_id AS "ZammadId", priority_id AS "TargetId"
              FROM zammad_priority_mappings
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var groupRows = await conn.QueryAsync<MappingRowDb>(new CommandDefinition(groupSql, cancellationToken: ct));
        var stateRows = await conn.QueryAsync<MappingRowDb>(new CommandDefinition(stateSql, cancellationToken: ct));
        var priorityRows = await conn.QueryAsync<MappingRowDb>(new CommandDefinition(prioritySql, cancellationToken: ct));

        return new MappingRowsBundle(
            GroupRows: groupRows.ToDictionary(r => r.ZammadId, r => r.TargetId),
            StateRows: stateRows.ToDictionary(r => r.ZammadId, r => r.TargetId),
            PriorityRows: priorityRows.ToDictionary(r => r.ZammadId, r => r.TargetId));
    }

    /// Row-DTO for Dapper. <c>sealed class { get; set; }</c> per the
    /// project Dapper convention.
    private sealed class MappingRowDb
    {
        public long ZammadId { get; set; }
        public Guid TargetId { get; set; }
    }

    private sealed record MappingRowsBundle(
        IReadOnlyDictionary<long, Guid> GroupRows,
        IReadOnlyDictionary<long, Guid> StateRows,
        IReadOnlyDictionary<long, Guid> PriorityRows);
}
