using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Mail.Polling;

/// Pulls new messages from Microsoft Graph for every inbound-mailbox source
/// (v0.0.66 — a queue can have many). Each source carries its own delta cursor
/// and health state in queue_inbound_mailboxes, so mailboxes advance and fail
/// independently. After a successful ingest commit we mark-read and (later, via
/// MailFinalizer) move the message into the processed folder so the inbox stays
/// clean.
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
        var repo = scope.ServiceProvider.GetRequiredService<IQueueInboundMailboxRepository>();
        var graph = scope.ServiceProvider.GetRequiredService<IGraphMailClient>();
        var ingest = scope.ServiceProvider.GetRequiredService<IMailIngestService>();
        var notifier = scope.ServiceProvider.GetRequiredService<ITicketListNotifier>();

        var intervalSeconds = await settings.GetAsync<int>(SettingKeys.Mail.PollingIntervalSeconds, ct);
        var batchSize = await settings.GetAsync<int>(SettingKeys.Mail.MaxBatchSize, ct);
        var markRead = await settings.GetAsync<bool>(SettingKeys.Mail.MarkAsReadOnIngest, ct);
        if (intervalSeconds < 10) intervalSeconds = 10;
        if (batchSize < 1) batchSize = 50;

        var queues = (await taxonomy.ListQueuesAsync(ct)).ToDictionary(q => q.Id);
        var sources = await repo.ListAllAsync(ct);
        foreach (var src in sources)
        {
            if (ct.IsCancellationRequested) break;
            // Queue must exist and be active.
            if (!queues.TryGetValue(src.QueueId, out var q) || !q.IsActive) continue;
            // Per-source pause switch. Delta-state is left intact so re-enabling
            // resumes from the last delta link instead of re-reading the inbox.
            if (!src.PollingEnabled) continue;
            if (string.IsNullOrWhiteSpace(src.MailboxAddress)) continue;
            if (string.IsNullOrWhiteSpace(src.FolderId)) continue;

            await PollSourceCoreAsync(
                src.Id, src.QueueId, q.Slug, src.MailboxAddress, src.FolderId!, batchSize,
                repo, graph, _logger, ct,
                ingest, markRead, notifier);
        }

        return TimeSpan.FromSeconds(intervalSeconds);
    }

    // Back-compat overload used by the unit tests (no ingest/mailbox-action wiring).
    internal static Task PollSourceCoreAsync(
        Guid sourceId,
        Guid queueId,
        string queueSlug,
        string mailbox,
        string folderId,
        int batchSize,
        IQueueInboundMailboxRepository repo,
        IGraphMailClient graph,
        ILogger logger,
        CancellationToken ct)
        => PollSourceCoreAsync(sourceId, queueId, queueSlug, mailbox, folderId, batchSize, repo, graph, logger, ct,
            ingest: null, markRead: false, notifier: null);

    internal static async Task PollSourceCoreAsync(
        Guid sourceId,
        Guid queueId,
        string queueSlug,
        string mailbox,
        string folderId,
        int batchSize,
        IQueueInboundMailboxRepository repo,
        IGraphMailClient graph,
        ILogger logger,
        CancellationToken ct,
        IMailIngestService? ingest,
        bool markRead,
        ITicketListNotifier? notifier)
    {
        var state = await repo.GetAsync(sourceId, ct);
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
            var page = await graph.ListInboxDeltaAsync(mailbox, folderId, state?.DeltaLink, batchSize, ct);
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
                            mailbox, m.Id, sourceId, graph, repo,
                            markRead, logger, ct);

                        // Push to any connected clients so the list refreshes
                        // without a manual reload. Best-effort — a failing hub
                        // must never break ingest.
                        if (notifier is not null && result.TicketId is { } tid)
                        {
                            try { await notifier.NotifyUpdatedAsync(tid, ct); }
                            catch (Exception hubEx)
                            {
                                logger.LogWarning(hubEx,
                                    "[MailPolling] ticket-list notify failed for ticketId={TicketId}", tid);
                            }
                        }
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

            await repo.SaveSuccessAsync(sourceId, page.DeltaLink ?? state?.DeltaLink, now, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = ex.GetType().Name + ": " + ex.Message;
            logger.LogWarning(ex,
                "[MailPolling] queue={Queue} mailbox={Mailbox} failed: {Error}",
                queueSlug, mailbox, msg);
            await repo.SaveFailureAsync(sourceId, msg, now, ct);
        }
    }

    // Mark-as-read is safe to run immediately (it doesn't change the Graph
    // message id). The Move step has been deferred to MailFinalizer, which
    // runs only after every attachment on the mail has reached Ready — see
    // the plan-file for the race-condition that drove this split.
    private static async Task HandleMailboxActionsAsync(
        string mailbox, string graphMessageId, Guid sourceId,
        IGraphMailClient graph, IQueueInboundMailboxRepository repo,
        bool markRead, ILogger logger, CancellationToken ct)
    {
        if (!markRead)
        {
            await repo.ClearMailboxActionErrorAsync(sourceId, ct);
            return;
        }

        try
        {
            await graph.MarkAsReadAsync(mailbox, graphMessageId, ct);
            await repo.ClearMailboxActionErrorAsync(sourceId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Mark-as-read failed for {Id}", graphMessageId);
            await repo.SaveMailboxActionErrorAsync(
                sourceId,
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
