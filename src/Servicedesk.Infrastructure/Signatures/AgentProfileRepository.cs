using Dapper;
using Npgsql;
using Servicedesk.Domain.Signatures;

namespace Servicedesk.Infrastructure.Signatures;

public sealed class AgentProfileRepository : IAgentProfileRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AgentProfileRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AgentProfile?> GetAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                id                  AS UserId,
                display_name        AS DisplayName,
                job_title           AS JobTitle,
                work_phone          AS WorkPhone,
                mobile_phone        AS MobilePhone,
                photo_blob_hash     AS PhotoBlobHash,
                photo_mime          AS PhotoMime,
                entra_synced_utc    AS EntraSyncedUtc
            FROM users
            WHERE id = @userId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AgentProfile>(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    public async Task<bool> UpsertOverrideAsync(
        Guid userId, string? displayName, string? jobTitle,
        string? workPhone, string? mobilePhone, CancellationToken ct)
    {
        const string sql = """
            UPDATE users
            SET display_name = @displayName,
                job_title    = @jobTitle,
                work_phone   = @workPhone,
                mobile_phone = @mobilePhone
            WHERE id = @userId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { userId, displayName, jobTitle, workPhone, mobilePhone }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task SetPhotoAsync(Guid userId, string? blobHash, string? mime, CancellationToken ct)
    {
        const string sql = "UPDATE users SET photo_blob_hash = @blobHash, photo_mime = @mime WHERE id = @userId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, blobHash, mime }, cancellationToken: ct));
    }

    public async Task StampEntraSyncedAsync(Guid userId, CancellationToken ct)
    {
        const string sql = "UPDATE users SET entra_synced_utc = now() WHERE id = @userId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AgentProfileListItem>> ListAllAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT
                id                              AS UserId,
                email                           AS Email,
                role_name                       AS RoleName,
                display_name                    AS DisplayName,
                job_title                       AS JobTitle,
                work_phone                      AS WorkPhone,
                mobile_phone                    AS MobilePhone,
                (photo_blob_hash IS NOT NULL)   AS HasPhoto,
                entra_synced_utc                AS EntraSyncedUtc
            FROM users
            WHERE role_name IN ('Agent', 'Admin') AND is_active = TRUE
            ORDER BY lower(coalesce(display_name, email))
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AgentProfileListItem>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }
}
