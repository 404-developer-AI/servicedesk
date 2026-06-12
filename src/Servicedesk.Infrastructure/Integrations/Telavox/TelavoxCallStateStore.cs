using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Dapper-backed implementation of <see cref="ITelavoxCallStateStore"/>.
/// Mirrors the column-alias + row-DTO conventions used by
/// <see cref="TelavoxAgentLinkStore"/>; every SELECT column gets an
/// <c>AS "PascalCase"</c> alias and the row-DTO is a
/// <c>sealed class { get; set; }</c>.
public sealed class TelavoxCallStateStore : ITelavoxCallStateStore
{
    private readonly NpgsqlDataSource _dataSource;

    public TelavoxCallStateStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<TelavoxCallStateSnapshot?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT user_id       AS "UserId",
                   last_call_id   AS "LastCallId",
                   last_state     AS "LastState",
                   last_direction AS "LastDirection",
                   last_seen_utc  AS "LastSeenUtc"
              FROM telavox_call_state
             WHERE user_id = @UserId
             LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TelavoxCallStateRow>(new CommandDefinition(
            sql, new { UserId = userId }, cancellationToken: ct));
        return row?.ToSnapshot();
    }

    public async Task UpsertAsync(
        Guid userId,
        string? lastCallId,
        string? lastState,
        string? lastDirection,
        DateTime lastSeenUtc,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO telavox_call_state (user_id, last_call_id, last_state, last_direction, last_seen_utc)
            VALUES (@UserId, @LastCallId, @LastState, @LastDirection, @LastSeenUtc)
            ON CONFLICT (user_id) DO UPDATE SET
                last_call_id   = EXCLUDED.last_call_id,
                last_state     = EXCLUDED.last_state,
                last_direction = EXCLUDED.last_direction,
                last_seen_utc  = EXCLUDED.last_seen_utc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            UserId = userId,
            LastCallId = lastCallId,
            LastState = lastState,
            LastDirection = lastDirection,
            LastSeenUtc = lastSeenUtc,
        }, cancellationToken: ct));
    }

    private sealed class TelavoxCallStateRow
    {
        public Guid UserId { get; set; }
        public string? LastCallId { get; set; }
        public string? LastState { get; set; }
        public string? LastDirection { get; set; }
        public DateTime LastSeenUtc { get; set; }

        public TelavoxCallStateSnapshot ToSnapshot() =>
            new(UserId, LastCallId, LastState, LastDirection, LastSeenUtc);
    }
}
