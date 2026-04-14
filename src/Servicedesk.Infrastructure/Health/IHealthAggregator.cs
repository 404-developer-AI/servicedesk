namespace Servicedesk.Infrastructure.Health;

public interface IHealthAggregator
{
    Task<HealthReport> CollectAsync(CancellationToken ct);
}
