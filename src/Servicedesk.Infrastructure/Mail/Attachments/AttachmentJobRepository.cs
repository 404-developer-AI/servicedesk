using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Mail.Attachments;

public sealed class AttachmentJobRepository : IAttachmentJobRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AttachmentJobRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AttachmentJobClaim?> ClaimNextAsync(DateTime nowUtc, CancellationToken ct)
    {
        // FOR UPDATE SKIP LOCKED lets multiple workers pull distinct rows
        // without waiting. The inner SELECT picks the oldest due Pending job;
        // the outer UPDATE flips it to Running and bumps attempt_count so a
        // crash between claim and completion shows up as a retry rather than
        // being lost. ReturningClause yields the payload the worker needs.
        const string sql = """
            UPDATE attachment_jobs
               SET state            = 'Running',
                   attempt_count    = attempt_count + 1,
                   updated_utc      = now()
             WHERE id = (
                 SELECT id FROM attachment_jobs
                  WHERE state = 'Pending'
                    AND next_attempt_utc <= @nowUtc
                  ORDER BY next_attempt_utc, id
                  FOR UPDATE SKIP LOCKED
                  LIMIT 1)
            RETURNING id             AS JobId,
                      kind           AS Kind,
                      payload::text  AS PayloadJson,
                      attempt_count  AS AttemptCount
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AttachmentJobClaim>(
            new CommandDefinition(sql, new { nowUtc }, cancellationToken: ct));
    }

    public async Task CompleteAsync(long jobId, TimeSpan duration, CancellationToken ct)
    {
        const string sql = """
            UPDATE attachment_jobs
               SET state = 'Succeeded', last_error = NULL, updated_utc = now()
             WHERE id = @jobId;
            INSERT INTO attachment_job_attempts
                (job_id, started_utc, finished_utc, outcome, duration_ms)
            VALUES
                (@jobId, now() - (@durationMs * interval '1 millisecond'),
                 now(), 'Succeeded', @durationMs);
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { jobId, durationMs = (int)duration.TotalMilliseconds },
            cancellationToken: ct));
    }

    public async Task ScheduleRetryAsync(long jobId, DateTime nextAttemptUtc, string error, TimeSpan duration, CancellationToken ct)
    {
        const string sql = """
            UPDATE attachment_jobs
               SET state            = 'Pending',
                   next_attempt_utc = @nextAttemptUtc,
                   last_error       = @error,
                   updated_utc      = now()
             WHERE id = @jobId;
            INSERT INTO attachment_job_attempts
                (job_id, started_utc, finished_utc, outcome, error_message, duration_ms)
            VALUES
                (@jobId, now() - (@durationMs * interval '1 millisecond'),
                 now(), 'Failed', @error, @durationMs);
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { jobId, nextAttemptUtc, error, durationMs = (int)duration.TotalMilliseconds },
            cancellationToken: ct));
    }

    public async Task DeadLetterAsync(long jobId, string error, TimeSpan duration, CancellationToken ct)
    {
        const string sql = """
            UPDATE attachment_jobs
               SET state = 'DeadLettered', last_error = @error, updated_utc = now()
             WHERE id = @jobId;
            INSERT INTO attachment_job_attempts
                (job_id, started_utc, finished_utc, outcome, error_message, duration_ms)
            VALUES
                (@jobId, now() - (@durationMs * interval '1 millisecond'),
                 now(), 'Failed', @error, @durationMs);
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { jobId, error, durationMs = (int)duration.TotalMilliseconds },
            cancellationToken: ct));
    }

    public async Task<int> CountPendingOlderThanAsync(TimeSpan threshold, DateTime nowUtc, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*) FROM attachment_jobs
             WHERE state = 'Pending'
               AND kind  = 'Ingest'
               AND created_utc < @cutoff
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql,
            new { cutoff = nowUtc - threshold }, cancellationToken: ct));
    }

    public async Task<int> CountDeadLetteredAsync(CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*) FROM attachment_jobs WHERE state = 'DeadLettered'";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<int> RequeueDeadLetteredAsync(DateTime nowUtc, CancellationToken ct)
    {
        const string sql = """
            UPDATE attachment_jobs
               SET state            = 'Pending',
                   attempt_count    = 0,
                   next_attempt_utc = @nowUtc,
                   updated_utc      = now()
             WHERE state = 'DeadLettered'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql,
            new { nowUtc }, cancellationToken: ct));
    }
}
