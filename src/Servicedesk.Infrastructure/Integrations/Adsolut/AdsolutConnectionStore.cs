using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutConnectionStore : IAdsolutConnectionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutConnectionStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AdsolutConnection?> GetAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AdsolutConnection>(new CommandDefinition(
            """
            SELECT
                authorized_subject       AS AuthorizedSubject,
                authorized_email         AS AuthorizedEmail,
                authorized_utc           AS AuthorizedUtc,
                last_refreshed_utc       AS LastRefreshedUtc,
                access_token_expires_utc AS AccessTokenExpiresUtc,
                last_refresh_error       AS LastRefreshError,
                last_refresh_error_utc   AS LastRefreshErrorUtc,
                updated_utc              AS UpdatedUtc,
                administration_id        AS AdministrationId,
                scopes_at_authorize      AS ScopesAtAuthorize
            FROM adsolut_connection
            WHERE id = 1
            """,
            cancellationToken: ct));
    }

    public async Task SaveAsync(AdsolutConnection connection, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_connection (
                id,
                authorized_subject,
                authorized_email,
                authorized_utc,
                last_refreshed_utc,
                access_token_expires_utc,
                last_refresh_error,
                last_refresh_error_utc,
                administration_id,
                scopes_at_authorize,
                updated_utc
            )
            VALUES (
                1,
                @AuthorizedSubject,
                @AuthorizedEmail,
                @AuthorizedUtc,
                @LastRefreshedUtc,
                @AccessTokenExpiresUtc,
                @LastRefreshError,
                @LastRefreshErrorUtc,
                @AdministrationId,
                @ScopesAtAuthorize,
                now()
            )
            ON CONFLICT (id) DO UPDATE SET
                authorized_subject       = EXCLUDED.authorized_subject,
                authorized_email         = EXCLUDED.authorized_email,
                authorized_utc           = EXCLUDED.authorized_utc,
                last_refreshed_utc       = EXCLUDED.last_refreshed_utc,
                access_token_expires_utc = EXCLUDED.access_token_expires_utc,
                last_refresh_error       = EXCLUDED.last_refresh_error,
                last_refresh_error_utc   = EXCLUDED.last_refresh_error_utc,
                administration_id        = EXCLUDED.administration_id,
                scopes_at_authorize      = EXCLUDED.scopes_at_authorize,
                updated_utc              = now()
            """,
            new
            {
                connection.AuthorizedSubject,
                connection.AuthorizedEmail,
                connection.AuthorizedUtc,
                connection.LastRefreshedUtc,
                connection.AccessTokenExpiresUtc,
                connection.LastRefreshError,
                connection.LastRefreshErrorUtc,
                connection.AdministrationId,
                connection.ScopesAtAuthorize,
            },
            cancellationToken: ct));
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM adsolut_connection WHERE id = 1",
            cancellationToken: ct));
    }
}
