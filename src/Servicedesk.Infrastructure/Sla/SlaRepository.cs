using Dapper;
using Npgsql;
using Servicedesk.Domain.Sla;

namespace Servicedesk.Infrastructure.Sla;

public sealed class SlaRepository : ISlaRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public SlaRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // ---------- Schemas ----------

    public async Task<IReadOnlyList<BusinessHoursSchema>> ListSchemasAsync(CancellationToken ct)
    {
        const string sql = "SELECT id, name, timezone, country_code, is_default FROM business_hours_schemas ORDER BY is_default DESC, name";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<(Guid Id, string Name, string Timezone, string CountryCode, bool IsDefault)>(
            new CommandDefinition(sql, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return Array.Empty<BusinessHoursSchema>();

        var ids = rows.Select(r => r.Id).ToArray();
        var slots = await LoadSlotsAsync(conn, ids, ct);
        var holidays = await LoadHolidaysAsync(conn, ids, null, ct);
        return rows
            .Select(r => new BusinessHoursSchema(
                r.Id, r.Name, r.Timezone, r.CountryCode, r.IsDefault,
                slots.TryGetValue(r.Id, out var s) ? s : Array.Empty<BusinessHoursSlot>(),
                holidays.TryGetValue(r.Id, out var h) ? h : Array.Empty<Holiday>()))
            .ToList();
    }

    public async Task<BusinessHoursSchema?> GetSchemaAsync(Guid id, CancellationToken ct)
    {
        const string sql = "SELECT id, name, timezone, country_code, is_default FROM business_hours_schemas WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(Guid Id, string Name, string Timezone, string CountryCode, bool IsDefault)?>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
        if (row is null) return null;
        var slotsMap = await LoadSlotsAsync(conn, new[] { id }, ct);
        var holidaysMap = await LoadHolidaysAsync(conn, new[] { id }, null, ct);
        return new BusinessHoursSchema(
            row.Value.Id, row.Value.Name, row.Value.Timezone, row.Value.CountryCode, row.Value.IsDefault,
            slotsMap.TryGetValue(id, out var s) ? s : Array.Empty<BusinessHoursSlot>(),
            holidaysMap.TryGetValue(id, out var h) ? h : Array.Empty<Holiday>());
    }

    public async Task<BusinessHoursSchema?> GetDefaultSchemaAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM business_hours_schemas WHERE is_default = TRUE LIMIT 1",
            cancellationToken: ct));
        if (id is null) return null;
        return await GetSchemaAsync(id.Value, ct);
    }

    public async Task<Guid> CreateSchemaAsync(string name, string timezone, string countryCode, bool isDefault, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (isDefault)
        {
            await conn.ExecuteAsync(new CommandDefinition("UPDATE business_hours_schemas SET is_default = FALSE WHERE is_default = TRUE", transaction: tx, cancellationToken: ct));
        }
        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "INSERT INTO business_hours_schemas (name, timezone, country_code, is_default) VALUES (@name, @timezone, @countryCode, @isDefault) RETURNING id",
            new { name, timezone, countryCode, isDefault }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task UpdateSchemaAsync(Guid id, string name, string timezone, string countryCode, bool isDefault, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (isDefault)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE business_hours_schemas SET is_default = FALSE WHERE is_default = TRUE AND id <> @id",
                new { id }, transaction: tx, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE business_hours_schemas SET name = @name, timezone = @timezone, country_code = @countryCode, is_default = @isDefault, updated_utc = now() WHERE id = @id",
            new { id, name, timezone, countryCode, isDefault }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task DeleteSchemaAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM business_hours_schemas WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task SetSlotsAsync(Guid schemaId, IReadOnlyList<(int Day, int Start, int End)> slots, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM business_hours_slots WHERE schema_id = @schemaId",
            new { schemaId }, transaction: tx, cancellationToken: ct));
        foreach (var s in slots)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO business_hours_slots (schema_id, day_of_week, start_minute, end_minute) VALUES (@schemaId, @day, @start, @end)",
                new { schemaId, day = s.Day, start = s.Start, end = s.End }, transaction: tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    private static async Task<Dictionary<Guid, List<BusinessHoursSlot>>> LoadSlotsAsync(NpgsqlConnection conn, Guid[] ids, CancellationToken ct)
    {
        const string sql = "SELECT id, schema_id, day_of_week, start_minute, end_minute FROM business_hours_slots WHERE schema_id = ANY(@ids) ORDER BY day_of_week, start_minute";
        var rows = await conn.QueryAsync<(long Id, Guid SchemaId, int DayOfWeek, int StartMinute, int EndMinute)>(
            new CommandDefinition(sql, new { ids }, cancellationToken: ct));
        var map = new Dictionary<Guid, List<BusinessHoursSlot>>();
        foreach (var r in rows)
        {
            if (!map.TryGetValue(r.SchemaId, out var list)) { list = new(); map[r.SchemaId] = list; }
            list.Add(new BusinessHoursSlot(r.Id, r.SchemaId, r.DayOfWeek, r.StartMinute, r.EndMinute));
        }
        return map;
    }

    private static async Task<Dictionary<Guid, List<Holiday>>> LoadHolidaysAsync(NpgsqlConnection conn, Guid[] ids, int? year, CancellationToken ct)
    {
        var sql = "SELECT id, schema_id, holiday_date, name, source, country_code FROM holidays WHERE schema_id = ANY(@ids)";
        if (year.HasValue) sql += " AND EXTRACT(YEAR FROM holiday_date) = @year";
        sql += " ORDER BY holiday_date";
        var rows = await conn.QueryAsync<(long Id, Guid SchemaId, DateTime HolidayDate, string Name, string Source, string CountryCode)>(
            new CommandDefinition(sql, new { ids, year }, cancellationToken: ct));
        var map = new Dictionary<Guid, List<Holiday>>();
        foreach (var r in rows)
        {
            if (!map.TryGetValue(r.SchemaId, out var list)) { list = new(); map[r.SchemaId] = list; }
            list.Add(new Holiday(r.Id, r.SchemaId, DateOnly.FromDateTime(r.HolidayDate), r.Name, r.Source, r.CountryCode));
        }
        return map;
    }

    // ---------- Holidays ----------

    public async Task<IReadOnlyList<Holiday>> ListHolidaysAsync(Guid schemaId, int? year, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var map = await LoadHolidaysAsync(conn, new[] { schemaId }, year, ct);
        return map.TryGetValue(schemaId, out var list) ? list : Array.Empty<Holiday>();
    }

    public async Task AddHolidayAsync(Guid schemaId, DateOnly date, string name, string source, string countryCode, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO holidays (schema_id, holiday_date, name, source, country_code)
            VALUES (@schemaId, @date, @name, @source, @countryCode)
            ON CONFLICT (schema_id, holiday_date) DO UPDATE SET name = EXCLUDED.name, source = EXCLUDED.source, country_code = EXCLUDED.country_code
            """, new { schemaId, date = date.ToDateTime(TimeOnly.MinValue), name, source, countryCode }, cancellationToken: ct));
    }

    public async Task DeleteHolidayAsync(long id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM holidays WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task ReplaceNagerHolidaysAsync(Guid schemaId, int year, string countryCode, IReadOnlyList<(DateOnly Date, string Name)> holidays, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM holidays WHERE schema_id = @schemaId AND source = 'nager' AND EXTRACT(YEAR FROM holiday_date) = @year",
            new { schemaId, year }, transaction: tx, cancellationToken: ct));
        foreach (var h in holidays)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO holidays (schema_id, holiday_date, name, source, country_code)
                VALUES (@schemaId, @date, @name, 'nager', @countryCode)
                ON CONFLICT (schema_id, holiday_date) DO UPDATE SET name = EXCLUDED.name, source = EXCLUDED.source, country_code = EXCLUDED.country_code
                """, new { schemaId, date = h.Date.ToDateTime(TimeOnly.MinValue), name = h.Name, countryCode }, transaction: tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    // ---------- Policies ----------

    public async Task<IReadOnlyList<SlaPolicy>> ListPoliciesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, queue_id AS QueueId, priority_id AS PriorityId, business_hours_schema_id AS BusinessHoursSchemaId,
                   first_response_minutes AS FirstResponseMinutes, resolution_minutes AS ResolutionMinutes, pause_on_pending AS PauseOnPending
            FROM sla_policies
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SlaPolicy>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<SlaPolicy?> GetPolicyAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, queue_id AS QueueId, priority_id AS PriorityId, business_hours_schema_id AS BusinessHoursSchemaId,
                   first_response_minutes AS FirstResponseMinutes, resolution_minutes AS ResolutionMinutes, pause_on_pending AS PauseOnPending
            FROM sla_policies WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<SlaPolicy>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<SlaPolicy?> FindPolicyAsync(Guid? queueId, Guid priorityId, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, queue_id AS QueueId, priority_id AS PriorityId, business_hours_schema_id AS BusinessHoursSchemaId,
                   first_response_minutes AS FirstResponseMinutes, resolution_minutes AS ResolutionMinutes, pause_on_pending AS PauseOnPending
            FROM sla_policies
            WHERE priority_id = @priorityId AND (queue_id = @queueId OR queue_id IS NULL)
            ORDER BY (queue_id IS NOT NULL) DESC
            LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<SlaPolicy>(new CommandDefinition(sql, new { queueId, priorityId }, cancellationToken: ct));
    }

    public async Task<Guid> UpsertPolicyAsync(Guid? queueId, Guid priorityId, Guid schemaId, int firstResponseMinutes, int resolutionMinutes, bool pauseOnPending, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO sla_policies (queue_id, priority_id, business_hours_schema_id, first_response_minutes, resolution_minutes, pause_on_pending)
            VALUES (@queueId, @priorityId, @schemaId, @firstResponseMinutes, @resolutionMinutes, @pauseOnPending)
            ON CONFLICT (COALESCE(queue_id, '00000000-0000-0000-0000-000000000000'::uuid), priority_id) DO UPDATE
                SET business_hours_schema_id = EXCLUDED.business_hours_schema_id,
                    first_response_minutes = EXCLUDED.first_response_minutes,
                    resolution_minutes = EXCLUDED.resolution_minutes,
                    pause_on_pending = EXCLUDED.pause_on_pending,
                    updated_utc = now()
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql,
            new { queueId, priorityId, schemaId, firstResponseMinutes, resolutionMinutes, pauseOnPending },
            cancellationToken: ct));
    }

    public async Task DeletePolicyAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM sla_policies WHERE id = @id", new { id }, cancellationToken: ct));
    }

    // ---------- Ticket SLA state ----------

    public async Task<TicketSlaState?> GetStateAsync(Guid ticketId, CancellationToken ct)
    {
        const string sql = """
            SELECT ticket_id AS TicketId, policy_id AS PolicyId,
                   first_response_deadline_utc AS FirstResponseDeadlineUtc,
                   resolution_deadline_utc AS ResolutionDeadlineUtc,
                   first_response_met_utc AS FirstResponseMetUtc,
                   resolution_met_utc AS ResolutionMetUtc,
                   first_response_business_minutes AS FirstResponseBusinessMinutes,
                   resolution_business_minutes AS ResolutionBusinessMinutes,
                   is_paused AS IsPaused, paused_since_utc AS PausedSinceUtc,
                   paused_accum_minutes AS PausedAccumMinutes,
                   last_recalc_utc AS LastRecalcUtc, updated_utc AS UpdatedUtc
            FROM ticket_sla_state WHERE ticket_id = @ticketId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TicketSlaState>(new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
    }

    public async Task UpsertStateAsync(TicketSlaState s, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO ticket_sla_state
                (ticket_id, policy_id, first_response_deadline_utc, resolution_deadline_utc,
                 first_response_met_utc, resolution_met_utc,
                 first_response_business_minutes, resolution_business_minutes,
                 is_paused, paused_since_utc, paused_accum_minutes, last_recalc_utc, updated_utc)
            VALUES (@TicketId, @PolicyId, @FirstResponseDeadlineUtc, @ResolutionDeadlineUtc,
                    @FirstResponseMetUtc, @ResolutionMetUtc,
                    @FirstResponseBusinessMinutes, @ResolutionBusinessMinutes,
                    @IsPaused, @PausedSinceUtc, @PausedAccumMinutes, @LastRecalcUtc, now())
            ON CONFLICT (ticket_id) DO UPDATE SET
                policy_id = EXCLUDED.policy_id,
                first_response_deadline_utc = EXCLUDED.first_response_deadline_utc,
                resolution_deadline_utc = EXCLUDED.resolution_deadline_utc,
                first_response_met_utc = EXCLUDED.first_response_met_utc,
                resolution_met_utc = EXCLUDED.resolution_met_utc,
                first_response_business_minutes = EXCLUDED.first_response_business_minutes,
                resolution_business_minutes = EXCLUDED.resolution_business_minutes,
                is_paused = EXCLUDED.is_paused,
                paused_since_utc = EXCLUDED.paused_since_utc,
                paused_accum_minutes = EXCLUDED.paused_accum_minutes,
                last_recalc_utc = EXCLUDED.last_recalc_utc,
                updated_utc = now()
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, s, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Guid>> ListActiveTicketIdsAsync(int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT t.id FROM tickets t
            LEFT JOIN ticket_sla_state s ON s.ticket_id = t.id
            WHERE t.is_deleted = FALSE AND t.closed_utc IS NULL
              AND (s.first_response_met_utc IS NULL OR s.resolution_met_utc IS NULL)
            ORDER BY t.updated_utc ASC
            LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { limit }, cancellationToken: ct));
        return rows.ToList();
    }

    // ---------- Queries ----------

    public async Task<IReadOnlyList<SlaLogRow>> QueryLogAsync(SlaLogFilter f, CancellationToken ct)
    {
        var sql = """
            SELECT t.id AS TicketId, t.number AS Number, t.subject AS Subject,
                   t.queue_id AS QueueId, q.name AS QueueName,
                   t.priority_id AS PriorityId, p.name AS PriorityName,
                   t.status_id AS StatusId, st.name AS StatusName,
                   t.created_utc AS CreatedUtc,
                   s.first_response_deadline_utc AS FirstResponseDeadlineUtc,
                   s.first_response_met_utc AS FirstResponseMetUtc,
                   s.resolution_deadline_utc AS ResolutionDeadlineUtc,
                   s.resolution_met_utc AS ResolutionMetUtc,
                   s.first_response_business_minutes AS FirstResponseBusinessMinutes,
                   s.resolution_business_minutes AS ResolutionBusinessMinutes,
                   COALESCE(s.is_paused, FALSE) AS IsPaused,
                   CASE WHEN s.first_response_met_utc IS NULL AND s.first_response_deadline_utc < now() THEN TRUE
                        WHEN s.first_response_met_utc > s.first_response_deadline_utc THEN TRUE ELSE FALSE END AS FirstResponseBreached,
                   CASE WHEN s.resolution_met_utc IS NULL AND s.resolution_deadline_utc < now() THEN TRUE
                        WHEN s.resolution_met_utc > s.resolution_deadline_utc THEN TRUE ELSE FALSE END AS ResolutionBreached
            FROM tickets t
            JOIN queues q ON q.id = t.queue_id
            JOIN priorities p ON p.id = t.priority_id
            JOIN statuses st ON st.id = t.status_id
            LEFT JOIN ticket_sla_state s ON s.ticket_id = t.id
            WHERE t.is_deleted = FALSE
            """;
        var parameters = new DynamicParameters();
        if (f.QueueId.HasValue) { sql += " AND t.queue_id = @QueueId"; parameters.Add("QueueId", f.QueueId); }
        if (f.PriorityId.HasValue) { sql += " AND t.priority_id = @PriorityId"; parameters.Add("PriorityId", f.PriorityId); }
        if (f.StatusId.HasValue) { sql += " AND t.status_id = @StatusId"; parameters.Add("StatusId", f.StatusId); }
        if (f.FromUtc.HasValue) { sql += " AND t.created_utc >= @FromUtc"; parameters.Add("FromUtc", f.FromUtc); }
        if (f.ToUtc.HasValue) { sql += " AND t.created_utc <= @ToUtc"; parameters.Add("ToUtc", f.ToUtc); }
        if (!string.IsNullOrWhiteSpace(f.Search)) { sql += " AND t.subject ILIKE @Search"; parameters.Add("Search", "%" + f.Search + "%"); }
        if (f.BreachedOnly == true)
        {
            sql += @"
                AND ((s.first_response_met_utc IS NULL AND s.first_response_deadline_utc < now())
                  OR (s.first_response_met_utc > s.first_response_deadline_utc)
                  OR (s.resolution_met_utc IS NULL AND s.resolution_deadline_utc < now())
                  OR (s.resolution_met_utc > s.resolution_deadline_utc))";
        }
        if (f.CursorNumber.HasValue) { sql += " AND t.number < @Cursor"; parameters.Add("Cursor", f.CursorNumber); }
        sql += " ORDER BY t.number DESC LIMIT @Limit";
        parameters.Add("Limit", f.Limit);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SlaLogRow>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<QueueAvgPickup>> AvgPickupPerQueueAsync(int days, CancellationToken ct)
    {
        const string sql = """
            SELECT q.id AS QueueId, q.name AS QueueName,
                   COUNT(s.ticket_id) AS TicketCount,
                   AVG(s.first_response_business_minutes)::DOUBLE PRECISION AS AvgBusinessMinutes
            FROM queues q
            LEFT JOIN tickets t ON t.queue_id = q.id AND t.created_utc >= now() - make_interval(days => @days)
                                AND t.is_deleted = FALSE
            LEFT JOIN ticket_sla_state s ON s.ticket_id = t.id AND s.first_response_met_utc IS NOT NULL
            WHERE q.is_active = TRUE
            GROUP BY q.id, q.name
            ORDER BY q.sort_order, q.name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<QueueAvgPickup>(new CommandDefinition(sql, new { days }, cancellationToken: ct));
        return rows.ToList();
    }
}
