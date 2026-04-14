using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Mail.Polling;

/// Pulls new messages from Microsoft Graph per queue with a configured
/// inbound mailbox. Step 6 wires the delta feed into MailIngestService so
/// each summary becomes a real ticket (or appends to an existing thread).
/// After a successful ingest commit we mark-read and move the message into
/// the processed folder so the inbox stays clean.
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
        var ingest = scope.ServiceProvider.GetRequiredService<IMailIngestService>();

        var intervalSeconds = await settings.GetAsync<int>(SettingKeys.Mail.PollingIntervalSeconds, ct);
        var batchSize = await settings.GetAsync<int>(SettingKeys.Mail.MaxBatchSize, ct);
        var markRead = await settings.GetAsync<bool>(SettingKeys.Mail.MarkAsReadOnIngest, ct);
        if (intervalSeconds < 10) intervalSeconds = 10;
        if (batchSize < 1) batchSize = 50;

        var queues = await taxonomy.ListQueuesAsync(ct);
        foreach (var q in queues)
        {
            if (ct.IsCancellationRequested) break;
            if (!q.IsActive) continue;
            if (string.IsNullOrWhiteSpace(q.InboundMailboxAddress)) continue;

            await PollQueueCoreAsync(
                q.Id, q.Slug, q.InboundMailboxAddress!, batchSize,
                stateRepo, graph, _logger, ct,
                ingest, markRead);
        }

        return TimeSpan.FromSeconds(intervalSeconds);
    }

    // Back-compat overload used by the stap-4 unit tests (no ingest/mailbox-action wiring).
    internal static Task PollQueueCoreAsync(
        Guid queueId,
        string queueSlug,
        string mailbox,
        int batchSize,
        IMailPollStateRepository stateRepo,
        IGraphMailClient graph,
        ILogger logger,
        CancellationToken ct)
        => PollQueueCoreAsync(queueId, queueSlug, mailbox, batchSize, stateRepo, graph, logger, ct,
            ingest: null, markRead: false);

    internal static async Task PollQueueCoreAsync(
        Guid queueId,
        string queueSlug,
        string mailbox,
        int batchSize,
        IMailPollStateRepository stateRepo,
        IGraphMailClient graph,
        ILogger logger,
        CancellationToken ct,
        IMailIngestService? ingest,
        bool markRead)
    {
        var state = await stateRepo.GetAsync(queueId, ct);
        if (state?.ConsecutiveFailures >= MaxConsecutiveFailuresBeforeSkip)
        {
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
                if (ct.IsCancellationRequested) break;
                logger.LogDebug(
                    "[MailPolling]   id={Id} from={From} subject={Subject} received={Received}",
                    m.Id, m.FromAddress, m.Subject, m.ReceivedUtc);

                if (ingest is null) continue;

                try
                {
                    var result = await ingest.IngestAsync(queueId, mailbox, m.Id, ct);
                    logger.LogInformation(
                        "[MailPolling]   -> outcome={Outcome} ticketId={TicketId} mailId={MailId} reason={Reason}",
                        result.Outcome, result.TicketId, result.MailMessageId, result.Reason ?? "-");

                    if (result.Outcome is MailIngestOutcome.Created or MailIngestOutcome.Appended)
                    {
                        await HandleMailboxActionsAsync(
                            mailbox, m.Id, queueId, graph, stateRepo,
                            markRead, logger, ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // One poisoned message must not break the delta cycle. Log and continue.
                    logger.LogWarning(ex,
                        "[MailPolling] queue={Queue} mailbox={Mailbox} ingest failed for id={Id} — continuing",
                        queueSlug, mailbox, m.Id);
                }
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

    // Mark-as-read is safe to run immediately (it doesn't change the Graph
    // message id). The Move step has been deferred to MailFinalizer, which
    // runs only after every attachment on the mail has reached Ready — see
    // the plan-file for the race-condition that drove this split.
    private static async Task HandleMailboxActionsAsync(
        string mailbox, string graphMessageId, Guid queueId,
        IGraphMailClient graph, IMailPollStateRepository stateRepo,
        bool markRead, ILogger logger, CancellationToken ct)
    {
        if (!markRead)
        {
            await stateRepo.ClearMailboxActionErrorAsync(queueId, ct);
            return;
        }

        try
        {
            await graph.MarkAsReadAsync(mailbox, graphMessageId, ct);
            await stateRepo.ClearMailboxActionErrorAsync(queueId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Mark-as-read failed for {Id}", graphMessageId);
            await stateRepo.SaveMailboxActionErrorAsync(
                queueId,
                $"mark-as-read: {ex.GetType().Name}: {ex.Message}",
                DateTime.UtcNow, ct);
        }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
