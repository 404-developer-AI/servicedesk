using Serilog.Core;
using Serilog.Events;

namespace Servicedesk.Infrastructure.Observability;

/// Serilog sink that captures Warning/Error log events from monitored
/// namespaces into the incident log. Writes are non-blocking — events are
/// queued on <see cref="IncidentLogBridge"/> and drained to Postgres by
/// <see cref="IncidentLogDrainService"/> on a background task. Events from
/// namespaces outside <see cref="SubsystemMap"/> are ignored (console/file
/// sinks still see them).
public sealed class IncidentLogSink : ILogEventSink
{
    /// Maps a Serilog SourceContext (namespace or class) to a subsystem key
    /// used by the Health aggregator. Anything not listed here is ignored.
    /// Matching is prefix-based — a logger named
    /// <c>Servicedesk.Infrastructure.Mail.Polling.MailPollingService</c>
    /// matches the <c>Servicedesk.Infrastructure.Mail.</c> entry.
    private static readonly (string Prefix, string Subsystem)[] SubsystemMap =
    {
        ("Servicedesk.Infrastructure.Mail.Ingest",      "mail-polling"),
        ("Servicedesk.Infrastructure.Mail.Polling",     "mail-polling"),
        ("Servicedesk.Infrastructure.Mail.Graph",       "mail-polling"),
        ("Servicedesk.Infrastructure.Mail.Attachments", "attachment-jobs"),
        ("Servicedesk.Infrastructure.Storage",          "blob-store"),
    };

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning) return;

        var source = TryGetSourceContext(logEvent);
        if (source is null) return;

        var subsystem = ResolveSubsystem(source);
        if (subsystem is null) return;

        var severity = logEvent.Level >= LogEventLevel.Error
            ? IncidentSeverity.Critical
            : IncidentSeverity.Warning;

        var message = logEvent.RenderMessage();
        var details = logEvent.Exception?.ToString();

        var report = new IncidentReport(subsystem, severity, message, details, ContextJson: null);
        // Fire-and-forget: TryWrite never blocks; DropOldest handles overflow.
        IncidentLogBridge.Writer.TryWrite(report);
    }

    private static string? TryGetSourceContext(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out var prop)) return null;
        if (prop is not ScalarValue sv) return null;
        return sv.Value as string;
    }

    private static string? ResolveSubsystem(string sourceContext)
    {
        foreach (var (prefix, subsystem) in SubsystemMap)
        {
            if (sourceContext.StartsWith(prefix, StringComparison.Ordinal))
                return subsystem;
        }
        return null;
    }
}
