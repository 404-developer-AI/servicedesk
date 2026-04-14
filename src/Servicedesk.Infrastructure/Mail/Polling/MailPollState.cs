namespace Servicedesk.Infrastructure.Mail.Polling;

public sealed record MailPollState(
    Guid QueueId,
    string? DeltaLink,
    DateTime? LastPolledUtc,
    string? LastError,
    int ConsecutiveFailures,
    DateTime UpdatedUtc,
    string? ProcessedFolderId = null,
    string? LastMailboxActionError = null,
    DateTime? LastMailboxActionErrorUtc = null);
