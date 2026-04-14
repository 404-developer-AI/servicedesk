using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Mail.Polling;

/// Performs the "move to processed folder" step of inbound-mail handling,
/// deferred until every attachment on a mail has reached <c>Ready</c>. Split
/// from <see cref="MailPollingService"/> so the same code path can be
/// triggered either from the attachment worker (after a job completes) or
/// from a periodic sweeper (catches mails without attachments and any rare
/// cases the hook missed).
public interface IMailFinalizer
{
    /// Invoked by the attachment worker after completing/dead-lettering a
    /// job. Fast-path: if <paramref name="mailId"/> is ready for finalize
    /// (all attachments Ready, not yet moved), finalize it. Otherwise
    /// returns without a Graph call.
    Task TryFinalizeMailAsync(Guid mailId, CancellationToken ct);

    /// Sweeper path: finds every mail currently ready for finalize and
    /// finalizes them one by one. Intended to run on a timer with a low
    /// frequency (tens of seconds).
    Task SweepAsync(CancellationToken ct);
}

public sealed class MailFinalizer : IMailFinalizer
{
    // Resolving the processed folder-id is a Graph round-trip per mailbox;
    // cache across calls so a sweep touching many mails for the same queue
    // doesn't re-ask. MailPollingService has its own identical cache — the
    // two don't share so we pay the lookup once per process per mailbox.
    private static readonly ConcurrentDictionary<string, string> FolderIdCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IMailMessageRepository _mail;
    private readonly IGraphMailClient _graph;
    private readonly IMailPollStateRepository _pollState;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ISettingsService _settings;
    private readonly ILogger<MailFinalizer> _logger;

    public MailFinalizer(
        IMailMessageRepository mail,
        IGraphMailClient graph,
        IMailPollStateRepository pollState,
        ITaxonomyRepository taxonomy,
        ISettingsService settings,
        ILogger<MailFinalizer> logger)
    {
        _mail = mail;
        _graph = graph;
        _pollState = pollState;
        _taxonomy = taxonomy;
        _settings = settings;
        _logger = logger;
    }

    public async Task TryFinalizeMailAsync(Guid mailId, CancellationToken ct)
    {
        var candidate = await _mail.GetIfReadyForFinalizeAsync(mailId, ct);
        if (candidate is null) return;

        var doMove = await _settings.GetAsync<bool>(SettingKeys.Mail.MoveOnIngest, ct);
        if (!doMove) return; // setting disabled — leave the flag NULL

        var folder = await _settings.GetAsync<string>(SettingKeys.Mail.ProcessedFolderName, ct)
            ?? "Servicedesk Verwerkt";
        await FinalizeOneAsync(candidate, folder, ct);
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        var doMove = await _settings.GetAsync<bool>(SettingKeys.Mail.MoveOnIngest, ct);
        if (!doMove) return;

        var batch = await _mail.ListReadyForFinalizeAsync(limit: 50, ct);
        if (batch.Count == 0) return;

        var folder = await _settings.GetAsync<string>(SettingKeys.Mail.ProcessedFolderName, ct)
            ?? "Servicedesk Verwerkt";

        foreach (var c in batch)
        {
            if (ct.IsCancellationRequested) break;
            await FinalizeOneAsync(c, folder, ct);
        }
    }

    private async Task FinalizeOneAsync(FinalizeCandidate c, string folderName, CancellationToken ct)
    {
        // Map mailbox → queueId so we can reuse mail_poll_state.processed_folder_id
        // caching. Multiple queues can't share an inbound mailbox (taxonomy
        // constraint), so the first match is correct.
        var queues = await _taxonomy.ListQueuesAsync(ct);
        var queue = queues.FirstOrDefault(q =>
            string.Equals(q.InboundMailboxAddress, c.MailboxAddress, StringComparison.OrdinalIgnoreCase));
        if (queue is null)
        {
            _logger.LogWarning(
                "[MailFinalizer] mailId={MailId} mailbox={Mailbox}: no queue matches, marking moved to stop retries.",
                c.MailId, c.MailboxAddress);
            await _mail.MarkMailboxMovedAsync(c.MailId, DateTime.UtcNow, ct);
            return;
        }

        try
        {
            var folderId = await ResolveFolderIdAsync(c.MailboxAddress, folderName, queue.Id, ct);
            await _graph.MoveAsync(c.MailboxAddress, c.GraphMessageId, folderId, ct);
            await _mail.MarkMailboxMovedAsync(c.MailId, DateTime.UtcNow, ct);
            await _pollState.ClearMailboxActionErrorAsync(queue.Id, ct);
            _logger.LogInformation(
                "[MailFinalizer] mailId={MailId} moved to processed folder (queue={QueueSlug})",
                c.MailId, queue.Slug);
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
            when (ex.ResponseStatusCode == 404
                || string.Equals(ex.Error?.Code, "ErrorItemNotFound", StringComparison.OrdinalIgnoreCase))
        {
            // Message already gone (user moved it manually, or we already
            // moved it on a previous attempt but didn't persist the flag
            // before a crash). Treat as done.
            await _mail.MarkMailboxMovedAsync(c.MailId, DateTime.UtcNow, ct);
            _logger.LogInformation(
                "[MailFinalizer] mailId={MailId} already absent in inbox (Graph 404) — marked moved.",
                c.MailId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[MailFinalizer] mailId={MailId} mailbox={Mailbox} move failed — will retry on next sweep.",
                c.MailId, c.MailboxAddress);
            await _pollState.SaveMailboxActionErrorAsync(
                queue.Id,
                $"finalize-move: {ex.GetType().Name}: {ex.Message}",
                DateTime.UtcNow, ct);
        }
    }

    private async Task<string> ResolveFolderIdAsync(
        string mailbox, string folderName, Guid queueId, CancellationToken ct)
    {
        var cacheKey = $"{mailbox}|{folderName}";
        if (FolderIdCache.TryGetValue(cacheKey, out var cached)) return cached;

        var state = await _pollState.GetAsync(queueId, ct);
        if (!string.IsNullOrWhiteSpace(state?.ProcessedFolderId))
        {
            FolderIdCache[cacheKey] = state.ProcessedFolderId!;
            return state.ProcessedFolderId!;
        }

        var folderId = await _graph.EnsureFolderAsync(mailbox, folderName, ct);
        await _pollState.SaveProcessedFolderIdAsync(queueId, folderId, ct);
        FolderIdCache[cacheKey] = folderId;
        return folderId;
    }
}
