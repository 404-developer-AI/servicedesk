namespace Servicedesk.Infrastructure.Health;

/// Overall rollup for the public status-only endpoint + the admin dashboard pill.
public enum HealthStatus
{
    Ok = 0,
    Warning = 1,
    Critical = 2,
}

/// One row on the admin health page. `Actions` are machine-readable
/// instructions the UI renders as buttons (e.g. reset-failures).
public sealed record SubsystemHealth(
    string Key,
    string Label,
    HealthStatus Status,
    string Summary,
    IReadOnlyList<HealthDetail> Details,
    IReadOnlyList<HealthAction> Actions);

/// Key/value row under a subsystem — "last error", "last polled", etc.
public sealed record HealthDetail(string Label, string? Value);

/// Machine-readable action the admin UI can invoke. `Endpoint` is an
/// app-relative POST URL; `ConfirmMessage` shows a dialog first when set.
public sealed record HealthAction(
    string Key,
    string Label,
    string Endpoint,
    string? ConfirmMessage);

public sealed record HealthReport(
    HealthStatus Status,
    IReadOnlyList<SubsystemHealth> Subsystems);
