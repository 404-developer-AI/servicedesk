using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public interface IAdsolutErpCustomerRepository
{
    /// Cache one resolved ERP customer (id → code/name). Upsert so a re-resolve
    /// refreshes a changed code.
    Task UpsertAsync(AdsolutErpCustomer customer, CancellationToken ct = default);

    /// Distinct contract customer GUIDs that are not yet in the resolution map —
    /// the work-list for the Contracts sync to fetch via the ERP Customers
    /// endpoint. Capped by <paramref name="limit"/> so a tick can't run away.
    Task<IReadOnlyList<Guid>> GetUnresolvedContractCustomerIdsAsync(int limit, CancellationToken ct = default);
}

public sealed class AdsolutErpCustomerRepository : IAdsolutErpCustomerRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutErpCustomerRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(AdsolutErpCustomer c, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_erp_customers (id, code, name, synced_utc)
            VALUES (@Id, @Code, @Name, now())
            ON CONFLICT (id) DO UPDATE SET
                code       = EXCLUDED.code,
                name       = EXCLUDED.name,
                synced_utc = now()
            """,
            new { c.Id, c.Code, c.Name },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Guid>> GetUnresolvedContractCustomerIdsAsync(
        int limit, CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 5000);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ids = await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT DISTINCT c.customer_adsolut_id
            FROM adsolut_contracts c
            WHERE c.customer_adsolut_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM adsolut_erp_customers ec WHERE ec.id = c.customer_adsolut_id)
            LIMIT @Limit
            """,
            new { Limit = safeLimit },
            cancellationToken: ct));
        return ids.ToList();
    }
}
