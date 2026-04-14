using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Observability;

/// Drains <see cref="IncidentLogBridge"/> into the <c>incidents</c> table via
/// <see cref="IIncidentLog"/>. Single-reader loop matching the channel's
/// options. Errors inside the drain are logged to the console sink (not the
/// incident sink, to avoid feedback loops).
public sealed class IncidentLogDrainService : BackgroundService
{
    private readonly IIncidentLog _log;
    private readonly ILogger<IncidentLogDrainService> _logger;

    public IncidentLogDrainService(IIncidentLog log, ILogger<IncidentLogDrainService> logger)
    {
        _log = log;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var report in IncidentLogBridge.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _log.ReportAsync(
                        report.Subsystem, report.Severity, report.Message,
                        report.Details, report.ContextJson, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IncidentLogDrainService failed to persist an incident");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
