namespace Servicedesk.Domain.Access;

public sealed record QueueAccessEntry(
    Guid UserId,
    Guid QueueId,
    DateTime CreatedUtc);
