using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Mail.Polling;

/// Pulls new messages from Microsoft Graph per queue with a configured
/// inbound mailbox. Step 4 scope: fetch + log only. Actual ticket/mail
/// conversion lands in step 5. Delta cursor is persisted per queue so a
/// restart doesn't re-pull the same history.
public sealed class MailPollingService : BackgroundService
{
    private const int MaxConsecutiveFailuresBeforeSkip = 5;

    private readonly IServiceProvider _services;
    private readonly ILogger<MailPollingService> _logger;

    public MailPollingService(IServiceProvider services, ILogger<MailPollingService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MailPollingService started.");
        // Tiny initial delay so DB bootstrapper / seeders finish first.
        await SafeDelayAsync(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MailPollingService cycle crashed — will retry.");
                delay = TimeSpan.FromSeconds(30);
            }

            await SafeDelayAsync(delay, stoppingToken);
        }

        _logger.LogInformation("MailPollingService stopped.");
    }

    private async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var taxonomy = scope.ServiceProvider.GetRequiredService<ITaxonomyRepository>();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IMailPollStateRepository>();
        var graph = scope.ServiceProvider.GetRequiredService<IGraphMailClient>();

        var intervalSeconds = await settings.GetAsync<int>(SettingKeys.Mail.PollingIntervalSeconds, ct);
        var batchSize = await settings.GetAsync<int>(SettingKeys.Mail.MaxBatchSize, ct);
        if (intervalSeconds < 10) intervalSeconds = 10;
        if (batchSize < 1) batchSize = 50;

        var queues = await taxonomy.ListQueuesAsync(ct);
        foreach (var q in queues)
        {
            if (ct.IsCancellationRequested) break;
            if (!q.IsActive) continue;
            if (string.IsNullOrWhiteSpace(q.InboundMailboxAddress)) continue;

            await PollQueueAsync(q.Id, q.Slug, q.InboundMailboxAddress!, batchSize, stateRepo, graph, ct);
        }

        return TimeSpan.FromSeconds(intervalSeconds);
    }

    private Task PollQueueAsync(
        Guid queueId,
        string queueSlug,
        string mailbox,
        int batchSize,
        IMailPollStateRepository stateRepo,
        IGraphMailClient graph,
        CancellationToken ct)
        => PollQueueCoreAsync(queueId, queueSlug, mailbox, batchSize, stateRepo, graph, _logger, ct);

    internal static async Task PollQueueCoreAsync(
        Guid queueId,
        string queueSlug,
        string mailbox,
        int batchSize,
        IMailPollStateRepository stateRepo,
        IGraphMailClient graph,
        ILogger logger,
        CancellationToken ct)
    {
        var state = await stateRepo.GetAsync(queueId, ct);
        if (state?.ConsecutiveFailures >= MaxConsecutiveFailuresBeforeSkip)
        {
            // Skip quietly; admin must clear last_error / reset failures via a manual action later.
            logger.LogWarning(
                "[MailPolling] queue={Queue} mailbox={Mailbox} skipped after {Failures} consecutive failures (last error: {Error})",
                queueSlug, mailbox, state.ConsecutiveFailures, state.LastError);
            return;
        }

        var now = DateTime.UtcNow;
        try
        {
            var page = await graph.ListInboxDeltaAsync(mailbox, state?.DeltaLink, batchSize, ct);
            logger.LogInformation(
                "[MailPolling] queue={Queue} mailbox={Mailbox} received {Count} message(s)",
                queueSlug, mailbox, page.Messages.Count);

            foreach (var m in page.Messages)
            {
                logger.LogDebug(
                    "[MailPolling]   id={Id} from={From} subject={Subject} received={Received}",
                    m.Id, m.FromAddress, m.Subject, m.ReceivedUtc);
            }

            await stateRepo.SaveSuccessAsync(queueId, page.DeltaLink ?? state?.DeltaLink, now, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = ex.GetType().Name + ": " + ex.Message;
            logger.LogWarning(ex,
                "[MailPolling] queue={Queue} mailbox={Mailbox} failed: {Error}",
                queueSlug, mailbox, msg);
            await stateRepo.SaveFailureAsync(queueId, msg, now, ct);
        }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
