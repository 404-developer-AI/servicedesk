using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Persistence.Taxonomy;

/// Seeds a minimal default taxonomy on startup so a fresh install has a
/// working set of statuses out of the box. Queues and priorities are fully
/// user-defined — admins create them in Settings. Every seeded row is
/// marked <c>is_system = TRUE</c>: admins can rename and re-color them
/// but not delete them. Seeding is idempotent — <c>ON CONFLICT (slug)
/// DO NOTHING</c> ensures replays are safe.
public sealed class TaxonomySeeder : IHostedService
{
    private const string Sql = """
        -- Queues are fully user-defined — clear any legacy system flag.
        UPDATE queues SET is_system = FALSE WHERE is_system = TRUE;

        -- Priorities are fully user-defined — no system defaults seeded.

        -- Default statuses — each pinned to a state_category so SLA and
        -- "open" filters keep working when admins rename the display labels.
        INSERT INTO statuses (name, slug, state_category, color, icon, sort_order, is_active, is_system, is_default) VALUES
            ('New',      'new',      'New',      '#60a5fa', 'sparkles',    10, TRUE, TRUE, TRUE),
            ('Open',     'open',     'Open',     '#7c7cff', 'circle-dot',  20, TRUE, TRUE, FALSE),
            ('Pending',  'pending',  'Pending',  '#f59e0b', 'hourglass',   30, TRUE, TRUE, FALSE),
            ('Resolved', 'resolved', 'Resolved', '#22c55e', 'check',       40, TRUE, TRUE, FALSE),
            ('Closed',   'closed',   'Closed',   '#64748b', 'archive',     50, TRUE, TRUE, FALSE)
        ON CONFLICT (slug) DO NOTHING;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<TaxonomySeeder> _logger;

    public TaxonomySeeder(NpgsqlDataSource dataSource, ILogger<TaxonomySeeder> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(Sql, cancellationToken: cancellationToken));
        _logger.LogInformation("Taxonomy seed complete (statuses).");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
