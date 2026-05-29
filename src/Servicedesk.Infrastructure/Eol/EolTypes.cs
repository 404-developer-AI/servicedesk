namespace Servicedesk.Infrastructure.Eol;

/// One end-of-life entry parsed from <c>endoflife.date</c>. The
/// <see cref="Product"/> + <see cref="Cycle"/> pair forms the natural
/// key — <c>("windows", "11-24h2")</c>, <c>("windows-server", "2022")</c>,
/// etc.
public sealed record EolReleaseRow(
    string Product,
    string Cycle,
    string? ReleaseLabel,
    DateTime? EolUtc,
    bool Lts);

/// Aggregate result of one refresh cycle. Counts split per product so
/// the audit-log payload tells an admin which feed moved data.
public sealed record EolRefreshOutcome(
    bool Success,
    int WindowsRows,
    int WindowsServerRows,
    int LatencyMs,
    string? ErrorCode,
    string? ErrorMessage);
