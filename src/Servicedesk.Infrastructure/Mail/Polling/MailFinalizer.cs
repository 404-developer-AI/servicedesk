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
    // cache across calls so a sweep touching many mails for the same mailbox
    // doesn't re-ask. MailPollingService has its own identical cache — the
    // two don't share so we pay the lookup once per process per mailbox.
    private static readonly ConcurrentDictionary<string, string> FolderIdCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IMailMessageRepository _mail;
    private readonly IGraphMailClient _graph;
    private readonly IQueueInboundMailboxRepository _sources;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ISettingsService _settings;
    private readonly ILogger<MailFinalizer> _logger;

    public MailFinalizer(
        IMailMessageRepository mail,
        IGraphMailClient graph,
        IQueueInboundMailboxRepository sources,
        ITaxonomyRepository taxonomy,
        ISettingsService settings,
        ILogger<MailFinalizer> logger)
    {
        _mail = mail;
        _graph = graph;
        _sources = sources;
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
        // Map mailbox → inbound source so we can attach the processed-folder id
        // and any move error to a real row. The processed folder is per-mailbox,
        // so the first source matching the mailbox is a valid anchor even when a
        // mailbox feeds the queue under several folders.
        var sources = await _sources.ListAllAsync(ct);
        var source = sources.FirstOrDefault(s =>
            string.Equals(s.MailboxAddress, c.MailboxAddress, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            _logger.LogWarning(
                "[MailFinalizer] mailId={MailId} mailbox={Mailbox}: no inbound source matches, marking moved to stop retries.",
                c.MailId, c.MailboxAddress);
            await _mail.MarkMailboxMovedAsync(c.MailId, DateTime.UtcNow, ct);
            return;
        }

        var queues = await _taxonomy.ListQueuesAsync(ct);
        var queueSlug = queues.FirstOrDefault(q => q.Id == source.QueueId)?.Slug ?? source.QueueId.ToString();

        try
        {
            var folderId = await ResolveFolderIdAsync(c.MailboxAddress, folderName, source.Id, ct);
            var moved = await TryMoveWithFolderRefreshAsync(
                c, folderName, source.Id, queueSlug, folderId, ct);
            await _mail.MarkMailboxMovedAsync(c.MailId, DateTime.UtcNow, ct);
            await _sources.ClearMailboxActionErrorAsync(source.Id, ct);
            _logger.LogInformation(
                moved
                    ? "[MailFinalizer] mailId={MailId} moved to processed folder (queue={QueueSlug})"
                    : "[MailFinalizer] mailId={MailId} already absent in inbox (Graph 404, folder valid) — marked moved (queue={QueueSlug})",
                c.MailId, queueSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[MailFinalizer] mailId={MailId} mailbox={Mailbox} move failed — will retry on next sweep.",
                c.MailId, c.MailboxAddress);
            await _sources.SaveMailboxActionErrorAsync(
                source.Id,
                $"finalize-move: {ex.GetType().Name}: {ex.Message}",
                DateTime.UtcNow, ct);
        }
    }

    /// Moves the message, self-healing a stale cached processed-folder id. A 404
    /// from Graph's Move is ambiguous — it can mean the message is gone OR that
    /// the cached destination folder no longer exists (deleted/renamed
    /// externally, or the source's mailbox changed). Treating every 404 as
    /// "message gone" would silently mark mails moved while they still sit in the
    /// inbox. So on a 404 we drop the cached folder id, re-ensure the folder
    /// (find-or-create), and retry once. Returns <c>true</c> when the message was
    /// actually moved, <c>false</c> when it was genuinely gone (404 persisted
    /// against a freshly-ensured, valid folder).
    private async Task<bool> TryMoveWithFolderRefreshAsync(
        FinalizeCandidate c, string folderName, Guid sourceId, string queueSlug,
        string folderId, CancellationToken ct)
    {
        try
        {
            await _graph.MoveAsync(c.MailboxAddress, c.GraphMessageId, folderId, ct);
            return true;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsNotFound(ex))
        {
            var fresh = await ForceResolveFolderIdAsync(c.MailboxAddress, folderName, sourceId, ct);
            // Folder id unchanged -> the destination was valid all along, so the
            // 404 really is the message itself. Caller marks it moved.
            if (string.Equals(fresh, folderId, StringComparison.Ordinal))
                return false;

            // The cached id was stale; the re-ensured folder is valid. Retry once.
            await _graph.MoveAsync(c.MailboxAddress, c.GraphMessageId, fresh, ct);
            _logger.LogInformation(
                "[MailFinalizer] mailId={MailId} processed-folder id was stale; refreshed and moved (queue={QueueSlug})",
                c.MailId, queueSlug);
            return true;
        }
    }

    private static bool IsNotFound(Microsoft.Graph.Models.ODataErrors.ODataError ex)
        => ex.ResponseStatusCode == 404
           || string.Equals(ex.Error?.Code, "ErrorItemNotFound", StringComparison.OrdinalIgnoreCase);

    /// Drops the cached processed-folder id (in-memory + persisted) and resolves
    /// it again from Graph, recreating the folder if it no longer exists. Used to
    /// recover from a stale id without waiting for a process restart.
    private async Task<string> ForceResolveFolderIdAsync(
        string mailbox, string folderName, Guid sourceId, CancellationToken ct)
    {
        FolderIdCache.TryRemove($"{mailbox}|{folderName}", out _);
        var folderId = await _graph.EnsureFolderAsync(mailbox, folderName, ct);
        await _sources.SaveProcessedFolderIdAsync(sourceId, folderId, ct);
        FolderIdCache[$"{mailbox}|{folderName}"] = folderId;
        return folderId;
    }

    private async Task<string> ResolveFolderIdAsync(
        string mailbox, string folderName, Guid sourceId, CancellationToken ct)
    {
        var cacheKey = $"{mailbox}|{folderName}";
        if (FolderIdCache.TryGetValue(cacheKey, out var cached)) return cached;

        var state = await _sources.GetAsync(sourceId, ct);
        if (!string.IsNullOrWhiteSpace(state?.ProcessedFolderId))
        {
            FolderIdCache[cacheKey] = state.ProcessedFolderId!;
            return state.ProcessedFolderId!;
        }

        var folderId = await _graph.EnsureFolderAsync(mailbox, folderName, ct);
        await _sources.SaveProcessedFolderIdAsync(sourceId, folderId, ct);
        FolderIdCache[cacheKey] = folderId;
        return folderId;
    }
}
