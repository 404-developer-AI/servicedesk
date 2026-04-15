using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Sla;

/// Seeds one default business-hours schema (Mon–Fri 09:00–17:00, Europe/Brussels)
/// so a fresh install can configure SLA policies immediately. Idempotent.
public sealed class SlaSeeder : IHostedService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IServiceProvider _sp;
    private readonly ILogger<SlaSeeder> _logger;

    public SlaSeeder(NpgsqlDataSource dataSource, IServiceProvider sp, ILogger<SlaSeeder> logger)
    {
        _dataSource = dataSource;
        _sp = sp;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var existing = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM business_hours_schemas", cancellationToken: ct));
        if (existing > 0)
        {
            _logger.LogInformation("SLA seeder: {Count} business-hours schema(s) already present, skipping.", existing);
            return;
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        var schemaId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO business_hours_schemas (name, timezone, country_code, is_default)
            VALUES ('Standard (Mon–Fri 09:00–17:00)', 'Europe/Brussels', 'BE', TRUE)
            RETURNING id
            """, transaction: tx, cancellationToken: ct));

        for (var day = 1; day <= 5; day++)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO business_hours_slots (schema_id, day_of_week, start_minute, end_minute)
                VALUES (@schemaId, @day, 540, 1020)
                """, new { schemaId, day }, transaction: tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        _logger.LogInformation("SLA seeder: created default business-hours schema {Id}.", schemaId);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
