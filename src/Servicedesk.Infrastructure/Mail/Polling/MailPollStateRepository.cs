using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Mail.Polling;

public sealed class MailPollStateRepository : IMailPollStateRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public MailPollStateRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<MailPollState?> GetAsync(Guid queueId, CancellationToken ct)
    {
        const string sql = """
            SELECT queue_id             AS QueueId,
                   delta_link            AS DeltaLink,
                   last_polled_utc       AS LastPolledUtc,
                   last_error            AS LastError,
                   consecutive_failures  AS ConsecutiveFailures,
                   updated_utc           AS UpdatedUtc
            FROM mail_poll_state WHERE queue_id = @queueId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<MailPollState>(
            new CommandDefinition(sql, new { queueId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<MailPollState>> ListAllAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT queue_id             AS QueueId,
                   delta_link            AS DeltaLink,
                   last_polled_utc       AS LastPolledUtc,
                   last_error            AS LastError,
                   consecutive_failures  AS ConsecutiveFailures,
                   updated_utc           AS UpdatedUtc
            FROM mail_poll_state
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MailPollState>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task ResetFailuresAsync(Guid queueId, CancellationToken ct)
    {
        const string sql = """
            UPDATE mail_poll_state
               SET last_error = NULL,
                   consecutive_failures = 0,
                   updated_utc = now()
             WHERE queue_id = @queueId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { queueId }, cancellationToken: ct));
    }

    public async Task SaveSuccessAsync(Guid queueId, string? deltaLink, DateTime polledUtc, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO mail_poll_state (queue_id, delta_link, last_polled_utc, last_error, consecutive_failures, updated_utc)
            VALUES (@queueId, @deltaLink, @polledUtc, NULL, 0, now())
            ON CONFLICT (queue_id) DO UPDATE
                SET delta_link = EXCLUDED.delta_link,
                    last_polled_utc = EXCLUDED.last_polled_utc,
                    last_error = NULL,
                    consecutive_failures = 0,
                    updated_utc = now()
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { queueId, deltaLink, polledUtc }, cancellationToken: ct));
    }

    public async Task SaveFailureAsync(Guid queueId, string error, DateTime polledUtc, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO mail_poll_state (queue_id, delta_link, last_polled_utc, last_error, consecutive_failures, updated_utc)
            VALUES (@queueId, NULL, @polledUtc, @error, 1, now())
            ON CONFLICT (queue_id) DO UPDATE
                SET last_polled_utc = EXCLUDED.last_polled_utc,
                    last_error = @error,
                    consecutive_failures = mail_poll_state.consecutive_failures + 1,
                    updated_utc = now()
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { queueId, polledUtc, error }, cancellationToken: ct));
    }
}
