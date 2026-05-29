namespace Servicedesk.Infrastructure.Eol;

public interface IEolDataRefreshService
{
    Task<EolRefreshOutcome> RunOnceAsync(string trigger, CancellationToken ct);
}
