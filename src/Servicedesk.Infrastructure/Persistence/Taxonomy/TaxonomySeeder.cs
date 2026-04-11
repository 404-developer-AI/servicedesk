using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Persistence.Taxonomy;

/// Seeds a minimal default taxonomy on startup so a fresh install has a
/// working queue, set of priorities and statuses out of the box. Every row
/// is marked <c>is_system = TRUE</c>: admins can rename and re-color them
/// but not delete them, so the app never lands in a state where no default
/// exists. Seeding is idempotent — <c>ON CONFLICT (slug) DO NOTHING</c>
/// ensures replays are safe.
public sealed class TaxonomySeeder : IHostedService
{
    private const string Sql = """
        -- Default queue
        INSERT INTO queues (name, slug, description, color, icon, sort_order, is_active, is_system)
        VALUES ('Default', 'default', 'Unassigned inbox for new tickets.', '#7c7cff', 'inbox', 0, TRUE, TRUE)
        ON CONFLICT (slug) DO NOTHING;

        -- Default priorities
        INSERT INTO priorities (name, slug, level, color, icon, sort_order, is_active, is_system) VALUES
            ('Low',    'low',    10, '#60a5fa', 'flag', 10, TRUE, TRUE),
            ('Normal', 'normal', 20, '#7c7cff', 'flag', 20, TRUE, TRUE),
            ('High',   'high',   30, '#f59e0b', 'flag', 30, TRUE, TRUE),
            ('Urgent', 'urgent', 40, '#ef4444', 'flag', 40, TRUE, TRUE)
        ON CONFLICT (slug) DO NOTHING;

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
        _logger.LogInformation("Taxonomy seed complete (queues, priorities, statuses).");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
