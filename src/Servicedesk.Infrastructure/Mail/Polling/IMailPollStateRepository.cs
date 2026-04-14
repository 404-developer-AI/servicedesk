namespace Servicedesk.Infrastructure.Mail.Polling;

public interface IMailPollStateRepository
{
    Task<MailPollState?> GetAsync(Guid queueId, CancellationToken ct);
    Task<IReadOnlyList<MailPollState>> ListAllAsync(CancellationToken ct);
    Task SaveSuccessAsync(Guid queueId, string? deltaLink, DateTime polledUtc, CancellationToken ct);
    Task SaveFailureAsync(Guid queueId, string error, DateTime polledUtc, CancellationToken ct);
    Task ResetFailuresAsync(Guid queueId, CancellationToken ct);
    Task SaveProcessedFolderIdAsync(Guid queueId, string folderId, CancellationToken ct);
    Task SaveMailboxActionErrorAsync(Guid queueId, string error, DateTime occurredUtc, CancellationToken ct);
    Task ClearMailboxActionErrorAsync(Guid queueId, CancellationToken ct);
}
