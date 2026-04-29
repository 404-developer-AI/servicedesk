using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutSyncStateStore : IAdsolutSyncStateStore
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutSyncStateStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AdsolutSyncState?> GetAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AdsolutSyncState>(new CommandDefinition(
            """
            SELECT
                last_full_sync_utc                  AS LastFullSyncUtc,
                last_delta_sync_utc                 AS LastDeltaSyncUtc,
                last_error                          AS LastError,
                last_error_utc                      AS LastErrorUtc,
                companies_seen                      AS CompaniesSeen,
                companies_upserted                  AS CompaniesUpserted,
                companies_skipped_loser_in_conflict AS CompaniesSkippedLoserInConflict,
                updated_utc                         AS UpdatedUtc,
                acknowledged_utc                    AS AcknowledgedUtc
            FROM adsolut_sync_state
            WHERE id = 1
            """,
            cancellationToken: ct));
    }

    public async Task AcknowledgeAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // INSERT-on-empty so the very first acknowledge (no tick has run yet)
        // still creates the singleton row instead of silently no-op'ing.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_sync_state (id, acknowledged_utc, updated_utc)
            VALUES (1, now(), now())
            ON CONFLICT (id) DO UPDATE SET
                acknowledged_utc = now(),
                updated_utc      = now()
            """,
            cancellationToken: ct));
    }

    public async Task SaveAsync(AdsolutSyncState state, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_sync_state (
                id,
                last_full_sync_utc,
                last_delta_sync_utc,
                last_error,
                last_error_utc,
                companies_seen,
                companies_upserted,
                companies_skipped_loser_in_conflict,
                updated_utc
            )
            VALUES (
                1,
                @LastFullSyncUtc,
                @LastDeltaSyncUtc,
                @LastError,
                @LastErrorUtc,
                @CompaniesSeen,
                @CompaniesUpserted,
                @CompaniesSkippedLoserInConflict,
                now()
            )
            ON CONFLICT (id) DO UPDATE SET
                last_full_sync_utc                  = EXCLUDED.last_full_sync_utc,
                last_delta_sync_utc                 = EXCLUDED.last_delta_sync_utc,
                last_error                          = EXCLUDED.last_error,
                last_error_utc                      = EXCLUDED.last_error_utc,
                companies_seen                      = EXCLUDED.companies_seen,
                companies_upserted                  = EXCLUDED.companies_upserted,
                companies_skipped_loser_in_conflict = EXCLUDED.companies_skipped_loser_in_conflict,
                updated_utc                         = now()
            """,
            new
            {
                state.LastFullSyncUtc,
                state.LastDeltaSyncUtc,
                state.LastError,
                state.LastErrorUtc,
                state.CompaniesSeen,
                state.CompaniesUpserted,
                state.CompaniesSkippedLoserInConflict,
            },
            cancellationToken: ct));
    }
}
