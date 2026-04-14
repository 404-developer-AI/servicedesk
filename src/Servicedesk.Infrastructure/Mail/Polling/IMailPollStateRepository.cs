namespace Servicedesk.Infrastructure.Mail.Polling;

public interface IMailPollStateRepository
{
    Task<MailPollState?> GetAsync(Guid queueId, CancellationToken ct);
    Task<IReadOnlyList<MailPollState>> ListAllAsync(CancellationToken ct);
    Task SaveSuccessAsync(Guid queueId, string? deltaLink, DateTime polledUtc, CancellationToken ct);
    Task SaveFailureAsync(Guid queueId, string error, DateTime polledUtc, CancellationToken ct);
    Task ResetFailuresAsync(Guid queueId, CancellationToken ct);
}
