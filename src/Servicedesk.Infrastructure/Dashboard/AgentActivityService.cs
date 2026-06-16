using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Dashboard;

public sealed class AgentActivityService : IAgentActivityService
{
    private readonly NpgsqlDataSource _dataSource;

    public AgentActivityService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<AgentActivity>> BuildSnapshotAsync(
        IReadOnlyDictionary<Guid, AgentPresenceState> presence,
        CancellationToken ct)
    {
        var agents = await ListAgentsAsync(ct);

        // Resolve every ticket id mentioned across all presence rows in a
        // single round-trip. Avoids N+1 across active agents.
        var allTicketIds = new HashSet<Guid>();
        foreach (var state in presence.Values)
        {
            if (Guid.TryParse(state.ViewingTicketId, out var v)) allTicketIds.Add(v);
            foreach (var id in state.RecentTicketIds)
            {
                if (Guid.TryParse(id, out var r)) allTicketIds.Add(r);
            }
        }

        var tickets = await GetTicketsAsync(allTicketIds, ct);
        var callStates = await GetCallStatesAsync(agents.Select(a => a.Id).ToArray(), ct);

        var result = new List<AgentActivity>(agents.Count);
        foreach (var agent in agents)
        {
            presence.TryGetValue(agent.Id, out var state);
            callStates.TryGetValue(agent.Id, out var rawCall);
            result.Add(Compose(agent, state, AgentCallStateCategorization.Categorize(rawCall), tickets));
        }
        return result;
    }

    public async Task<AgentActivity?> BuildForUserAsync(
        Guid userId,
        AgentPresenceState presence,
        CancellationToken ct)
    {
        var agent = await GetAgentAsync(userId, ct);
        if (agent is null) return null;

        var ids = new HashSet<Guid>();
        if (Guid.TryParse(presence.ViewingTicketId, out var v)) ids.Add(v);
        foreach (var id in presence.RecentTicketIds)
        {
            if (Guid.TryParse(id, out var r)) ids.Add(r);
        }

        var tickets = await GetTicketsAsync(ids, ct);
        var callStates = await GetCallStatesAsync(new[] { userId }, ct);
        callStates.TryGetValue(userId, out var rawCall);
        return Compose(agent, presence, AgentCallStateCategorization.Categorize(rawCall), tickets);
    }

    private static AgentActivity Compose(
        AgentRow agent,
        AgentPresenceState? state,
        string callState,
        IReadOnlyDictionary<Guid, AgentActivityTicket> tickets)
    {
        AgentActivityTicket? viewing = null;
        var recent = new List<AgentActivityTicket>();

        if (state is not null)
        {
            if (Guid.TryParse(state.ViewingTicketId, out var vId)
                && tickets.TryGetValue(vId, out var vTicket))
            {
                viewing = vTicket;
            }
            foreach (var id in state.RecentTicketIds)
            {
                if (!Guid.TryParse(id, out var rId)) continue;
                // Don't duplicate the actively-viewed ticket inside Recent —
                // the UI surfaces it once in the "viewing" slot with the
                // colored pill.
                if (viewing is not null && rId == viewing.Id) continue;
                if (tickets.TryGetValue(rId, out var rTicket))
                {
                    recent.Add(rTicket);
                }
            }
            recent.Sort((a, b) => a.Number.CompareTo(b.Number));
        }

        return new AgentActivity(
            agent.Id,
            agent.Email,
            agent.RoleName,
            state?.Online ?? false,
            callState,
            viewing,
            recent);
    }

    private async Task<IReadOnlyList<AgentRow>> ListAgentsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, email AS Email, role_name AS RoleName
            FROM users WHERE role_name IN ('Agent', 'Admin')
            ORDER BY email
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AgentRow>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    private async Task<AgentRow?> GetAgentAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, email AS Email, role_name AS RoleName
            FROM users WHERE id = @id AND role_name IN ('Agent', 'Admin')
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AgentRow>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
    }

    private async Task<IReadOnlyDictionary<Guid, AgentActivityTicket>> GetTicketsAsync(
        IReadOnlyCollection<Guid> ticketIds,
        CancellationToken ct)
    {
        if (ticketIds.Count == 0)
        {
            return new Dictionary<Guid, AgentActivityTicket>();
        }

        // queue_id is carried so the broadcaster / snapshot endpoint can
        // mask the ticket for viewers without access to that queue. It is
        // never serialized (JsonIgnore on the DTO).
        const string sql = """
            SELECT t.id              AS Id,
                   t.number          AS Number,
                   t.subject         AS Subject,
                   s.name            AS StatusName,
                   s.color           AS StatusColor,
                   s.state_category  AS StatusStateCategory,
                   t.queue_id        AS QueueId
            FROM tickets t
            JOIN statuses s ON s.id = t.status_id
            WHERE t.id = ANY(@ids) AND t.is_deleted = FALSE
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TicketRow>(
            new CommandDefinition(sql, new { ids = ticketIds.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(
            r => r.Id,
            r => new AgentActivityTicket(
                r.Id, r.Number, r.Subject,
                r.StatusName, r.StatusColor, r.StatusStateCategory,
                Restricted: false, QueueId: r.QueueId));
    }

    private async Task<IReadOnlyDictionary<Guid, string?>> GetCallStatesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct)
    {
        if (userIds.Count == 0) return new Dictionary<Guid, string?>();

        // telavox_call_state may not exist on installs that have never had
        // the Telavox integration provisioned — the bootstrapper creates
        // the table lazily. Tolerate the missing-table case and return an
        // empty map so the tile still works for vanilla installs.
        const string sql = """
            SELECT user_id AS UserId, last_state AS LastState
            FROM telavox_call_state
            WHERE user_id = ANY(@ids)
            """;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var rows = await conn.QueryAsync<CallStateRow>(
                new CommandDefinition(sql, new { ids = userIds.ToArray() }, cancellationToken: ct));
            return rows.ToDictionary(r => r.UserId, r => (string?)r.LastState);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            return new Dictionary<Guid, string?>();
        }
    }

    private sealed class AgentRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
        public string RoleName { get; set; } = "";
    }

    private sealed class TicketRow
    {
        public Guid Id { get; set; }
        public long Number { get; set; }
        public string Subject { get; set; } = "";
        public string StatusName { get; set; } = "";
        public string StatusColor { get; set; } = "";
        public string StatusStateCategory { get; set; } = "";
        public Guid QueueId { get; set; }
    }

    private sealed class CallStateRow
    {
        public Guid UserId { get; set; }
        public string? LastState { get; set; }
    }
}
