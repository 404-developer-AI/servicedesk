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
    string? Reason);
