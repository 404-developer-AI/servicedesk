using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Activity;

/// Listens on the Postgres NOTIFY channel that the activity-feed triggers
/// emit. Every inserted row in <c>agent_activity_events</c> sends a
/// notification with the new row's id; this worker enriches the row via
/// <see cref="IActivityFeedQuery"/> and pushes it to admin clients via
/// <see cref="IActivityBroadcaster"/>.
///
/// The listener owns a single dedicated NpgsqlConnection (Postgres
/// requires that LISTEN happens on a connection held open for the
/// listener's lifetime). On disconnect we reconnect with exponential
/// backoff so a transient outage does not silently drop events.
public sealed class ActivityListenerWorker : BackgroundService
{
    private const string Channel = "agent_activity_event";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ActivityListenerWorker> _logger;

    public ActivityListenerWorker(
        NpgsqlDataSource dataSource,
        IServiceScopeFactory scopeFactory,
        ILogger<ActivityListenerWorker> logger)
    {
        _dataSource = dataSource;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromMinutes(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenOnceAsync(stoppingToken);
                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ActivityListenerWorker lost its LISTEN connection; reconnecting in {DelaySeconds}s.",
                    (int)delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
            }
        }
    }

    private async Task ListenOnceAsync(CancellationToken stoppingToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(stoppingToken);
        conn.Notification += OnNotification;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"LISTEN {Channel}";
        await cmd.ExecuteNonQueryAsync(stoppingToken);

        _logger.LogInformation("ActivityListenerWorker subscribed to '{Channel}'.", Channel);

        // WaitAsync blocks until a notification arrives; the Notification
        // event fires the inline handler which dispatches enrichment +
        // broadcast on the thread-pool so we never starve the listener.
        while (!stoppingToken.IsCancellationRequested)
        {
            await conn.WaitAsync(stoppingToken);
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        if (!long.TryParse(e.Payload, out var id))
        {
            return;
        }

        // Fire-and-forget enrichment so a slow broadcaster never blocks
        // the next NOTIFY. Errors are logged and swallowed.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var query = scope.ServiceProvider.GetRequiredService<IActivityFeedQuery>();
                var broadcaster = scope.ServiceProvider.GetRequiredService<IActivityBroadcaster>();

                var entry = await query.GetByIdAsync(id, CancellationToken.None);
                if (entry is null) return;
                await broadcaster.BroadcastAsync(entry, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ActivityListenerWorker failed to broadcast activity event {Id}.", id);
            }
        });
    }
}
