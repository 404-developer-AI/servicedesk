using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Dapper-backed implementation of <see cref="ITelavoxAgentLinkStore"/>.
/// All SELECTs alias every column to PascalCase per the project Dapper
/// convention; underscore-matching is intentionally not relied upon.
public sealed class TelavoxAgentLinkStore : ITelavoxAgentLinkStore
{
    private readonly NpgsqlDataSource _dataSource;

    public TelavoxAgentLinkStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<TelavoxAgentLink>> ListAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT id                  AS "Id",
                   user_id             AS "UserId",
                   telavox_extension   AS "TelavoxExtension",
                   telavox_user_id     AS "TelavoxUserId",
                   capi_user_email     AS "CapiUserEmail",
                   provisioned_utc     AS "ProvisionedUtc",
                   last_poll_utc       AS "LastPollUtc",
                   last_poll_error     AS "LastPollError",
                   consecutive_errors  AS "ConsecutiveErrors"
              FROM telavox_agent_links
             ORDER BY provisioned_utc DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TelavoxAgentLinkRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<TelavoxAgentLink?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id                  AS "Id",
                   user_id             AS "UserId",
                   telavox_extension   AS "TelavoxExtension",
                   telavox_user_id     AS "TelavoxUserId",
                   capi_user_email     AS "CapiUserEmail",
                   provisioned_utc     AS "ProvisionedUtc",
                   last_poll_utc       AS "LastPollUtc",
                   last_poll_error     AS "LastPollError",
                   consecutive_errors  AS "ConsecutiveErrors"
              FROM telavox_agent_links
             WHERE user_id = @UserId
             LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TelavoxAgentLinkRow>(new CommandDefinition(
            sql, new { UserId = userId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task UpsertAsync(TelavoxAgentLink link, CancellationToken ct = default)
    {
        // The UNIQUE constraint on user_id makes ON CONFLICT (user_id) the
        // single insert path. provisioned_utc stays the original on update
        // (audit-friendly); updated_utc bumps every write.
        const string sql = """
            INSERT INTO telavox_agent_links (
                user_id, telavox_extension, telavox_user_id, capi_user_email,
                provisioned_utc, last_poll_utc, last_poll_error, consecutive_errors,
                created_utc, updated_utc)
            VALUES (
                @UserId, @TelavoxExtension, @TelavoxUserId, @CapiUserEmail,
                @ProvisionedUtc, @LastPollUtc, @LastPollError, @ConsecutiveErrors,
                now(), now())
            ON CONFLICT (user_id) DO UPDATE SET
                telavox_extension  = EXCLUDED.telavox_extension,
                telavox_user_id    = EXCLUDED.telavox_user_id,
                capi_user_email    = EXCLUDED.capi_user_email,
                provisioned_utc    = EXCLUDED.provisioned_utc,
                last_poll_utc      = EXCLUDED.last_poll_utc,
                last_poll_error    = EXCLUDED.last_poll_error,
                consecutive_errors = EXCLUDED.consecutive_errors,
                updated_utc        = now()
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            link.UserId,
            link.TelavoxExtension,
            link.TelavoxUserId,
            link.CapiUserEmail,
            link.ProvisionedUtc,
            link.LastPollUtc,
            link.LastPollError,
            link.ConsecutiveErrors,
        }, cancellationToken: ct));
    }

    public async Task<int> DeleteByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM telavox_agent_links WHERE user_id = @UserId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            sql, new { UserId = userId }, cancellationToken: ct));
    }

    public async Task UpdatePollOutcomeAsync(
        Guid userId,
        DateTime lastPollUtc,
        string? lastPollError,
        int consecutiveErrors,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE telavox_agent_links
               SET last_poll_utc      = @LastPollUtc,
                   last_poll_error    = @LastPollError,
                   consecutive_errors = @ConsecutiveErrors,
                   updated_utc        = now()
             WHERE user_id = @UserId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            UserId = userId,
            LastPollUtc = lastPollUtc,
            LastPollError = lastPollError,
            ConsecutiveErrors = consecutiveErrors,
        }, cancellationToken: ct));
    }

    /// Row-DTO for Dapper. <c>sealed class { get; set; }</c> per the project
    /// Dapper convention — record-structs and positional records are both
    /// known to trip Dapper's nullable-handling and constructor-ordering.
    private sealed class TelavoxAgentLinkRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string TelavoxExtension { get; set; } = string.Empty;
        public string TelavoxUserId { get; set; } = string.Empty;
        public string CapiUserEmail { get; set; } = string.Empty;
        public DateTime ProvisionedUtc { get; set; }
        public DateTime? LastPollUtc { get; set; }
        public string? LastPollError { get; set; }
        public int ConsecutiveErrors { get; set; }

        public TelavoxAgentLink ToDomain() => new(
            Id, UserId, TelavoxExtension, TelavoxUserId, CapiUserEmail,
            ProvisionedUtc, LastPollUtc, LastPollError, ConsecutiveErrors);
    }
}
