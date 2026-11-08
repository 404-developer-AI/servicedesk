namespace Servicedesk.Infrastructure.Mail.Ingest;

public interface IMailIngestService
{
    Task<MailIngestResult> IngestAsync(
        Guid queueId, string queueMailbox, string graphMessageId, CancellationToken ct);
}

public enum MailIngestOutcome
{
    Created,
    Appended,
    Deduplicated,
    SkippedLoop,
    SkippedAutoSubmitted,
    SkippedOwnMailbox,
    SkippedNoDefaults,
    SkippedNotFound,
}

public sealed record MailIngestResult(
    MailIngestOutcome Outcome,
    Guid? MailMessageId,
    Guid? TicketId,
    long? EventId,
    string? Reason)
{
    /// Deduplicated only: the earlier copy came in through this same mailbox
    /// (typically a delta replay of the very same message). Its own mail row
    /// owns the move-to-processed step, so the poller must leave it alone.
    public bool DuplicateInSameMailbox { get; init; }
}
