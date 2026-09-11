using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// Dapper implementation of <see cref="IRemoteDesktopRepository"/>. Every
/// filter value travels as a parameter; the only SQL that varies is a
/// fixed ILIKE block toggled by whether a search term is present.
public sealed class RemoteDesktopRepository : IRemoteDesktopRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public RemoteDesktopRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string RowSelect = """
        SELECT a.id                 AS Id,
               a.trmm_agent_id      AS TrmmAgentId,
               a.hostname           AS Hostname,
               a.agent_type         AS AgentType,
               a.os_name            AS OsName,
               a.os_family          AS OsFamily,
               a.os_build           AS OsBuild,
               a.online             AS Online,
               a.last_seen_utc      AS LastSeenUtc,
               s.name               AS SiteName,
               a.trmm_client_id     AS TrmmClientId,
               c.name               AS ClientName,
               c.code               AS ClientCode,
               c.company_id         AS CompanyId,
               co.name              AS CompanyName,
               r.status             AS Status,
               r.check_status       AS CheckStatus,
               r.retcode            AS Retcode,
               r.stdout             AS Stdout,
               r.stderr             AS Stderr,
               r.last_login_local   AS LastLoginLocal,
               r.last_login_kind    AS LastLoginKind,
               r.last_login_user    AS LastLoginUser,
               r.last_run_utc       AS LastRunUtc,
               r.fetched_utc        AS FetchedUtc,
               r.fetch_error        AS FetchError
          FROM trmm_agents a
          JOIN trmm_clients c        ON c.trmm_client_id = a.trmm_client_id
          JOIN trmm_sites s          ON s.trmm_site_id   = a.trmm_site_id
          LEFT JOIN companies co     ON co.id = c.company_id
          LEFT JOIN trmm_agent_rds r ON r.trmm_agent_id = a.trmm_agent_id
         WHERE (a.agent_type = 'server' OR r.trmm_agent_id IS NOT NULL)
        """;

    public async Task<IReadOnlyList<RemoteDesktopRow>> ListAsync(string? search, CancellationToken ct)
    {
        var term = (search ?? string.Empty).Trim();
        var sql = RowSelect;
        if (term.Length > 0)
        {
            sql += """

                   AND (a.hostname ILIKE @like
                     OR c.name     ILIKE @like
                     OR coalesce(c.code, '') ILIKE @like
                     OR coalesce(co.name, '') ILIKE @like)
                """;
        }
        sql += "\n ORDER BY coalesce(c.code, ''), c.name, a.hostname";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RemoteDesktopRow>(new CommandDefinition(
            sql, new { like = "%" + EscapeLike(term) + "%" }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ClientNoteSummary>> ListNoteSummariesAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ClientNoteSummary>(new CommandDefinition(
            """
            SELECT trmm_client_id   AS TrmmClientId,
                   COUNT(*)::int    AS NoteCount,
                   MAX(created_utc) AS LastNoteUtc
              FROM trmm_client_notes
             GROUP BY trmm_client_id
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<RemoteDesktopSyncState> GetRdsSyncStateAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RemoteDesktopSyncState>(new CommandDefinition(
            """
            SELECT last_rds_sync_utc AS LastRdsSyncUtc,
                   last_rds_status   AS LastRdsStatus,
                   last_rds_error    AS LastRdsError
              FROM trmm_sync_state
             WHERE id = 'singleton'
            """, cancellationToken: ct));
        return row ?? new RemoteDesktopSyncState();
    }

    public async Task<bool> ClientExistsAsync(long trmmClientId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM trmm_clients WHERE trmm_client_id = @id)",
            new { id = trmmClientId }, cancellationToken: ct));
    }

    private const string NoteSelect = """
        SELECT n.id             AS Id,
               n.trmm_client_id AS TrmmClientId,
               n.body           AS Body,
               n.author_id      AS AuthorId,
               u.email          AS AuthorEmail,
               n.created_utc    AS CreatedUtc,
               n.updated_utc    AS UpdatedUtc
          FROM trmm_client_notes n
          LEFT JOIN users u ON u.id = n.author_id
        """;

    public async Task<IReadOnlyList<ClientNote>> ListNotesAsync(long trmmClientId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ClientNote>(new CommandDefinition(
            NoteSelect + " WHERE n.trmm_client_id = @id ORDER BY n.created_utc DESC, n.id DESC",
            new { id = trmmClientId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<ClientNote?> GetNoteAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ClientNote>(new CommandDefinition(
            NoteSelect + " WHERE n.id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<ClientNote> AddNoteAsync(long trmmClientId, Guid authorId, string body, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO trmm_client_notes (trmm_client_id, body, author_id, created_utc, updated_utc)
            VALUES (@trmmClientId, @body, @authorId, now(), now())
            RETURNING id
            """,
            new { trmmClientId, body, authorId }, cancellationToken: ct));
        return (await conn.QuerySingleAsync<ClientNote>(new CommandDefinition(
            NoteSelect + " WHERE n.id = @id", new { id }, cancellationToken: ct)));
    }

    public async Task<ClientNote?> UpdateNoteAsync(Guid id, string body, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE trmm_client_notes SET body = @body, updated_utc = now() WHERE id = @id",
            new { id, body }, cancellationToken: ct));
        if (affected == 0) return null;
        return await conn.QuerySingleOrDefaultAsync<ClientNote>(new CommandDefinition(
            NoteSelect + " WHERE n.id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<bool> DeleteNoteAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM trmm_client_notes WHERE id = @id", new { id }, cancellationToken: ct));
        return affected > 0;
    }

    /// Escapes the LIKE metacharacters in a user term so "100%" matches
    /// a literal percent sign instead of acting as a wildcard.
    private static string EscapeLike(string s) =>
        s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
