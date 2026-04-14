using System.Threading.Channels;

namespace Servicedesk.Infrastructure.Observability;

/// Static bridge between the Serilog sink (registered at host-build time,
/// outside DI) and the IncidentLogDrainService (runs in DI with access to
/// the database). Serilog's sink contract is synchronous, so the sink posts
/// incident reports to a bounded in-memory channel and returns immediately;
/// the drain service batches them into Postgres on a worker task. Bounded
/// at 1024 so a sudden log storm cannot blow up memory — excess events are
/// dropped (see <see cref="BoundedChannelFullMode.DropOldest"/>).
public static class IncidentLogBridge
{
    private static readonly Channel<IncidentReport> _channel = Channel.CreateBounded<IncidentReport>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public static ChannelWriter<IncidentReport> Writer => _channel.Writer;
    public static ChannelReader<IncidentReport> Reader => _channel.Reader;
}

public sealed record IncidentReport(
    string Subsystem,
    IncidentSeverity Severity,
    string Message,
    string? Details,
    string? ContextJson);
