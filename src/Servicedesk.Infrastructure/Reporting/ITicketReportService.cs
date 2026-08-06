namespace Servicedesk.Infrastructure.Reporting;

/// Read-only ticket statistics for the Reporting API (v0.0.96) — a
/// secret-gated machine-to-machine surface, so this service exposes only
/// aggregates and the minimal per-ticket fields (number + subject).
public interface ITicketReportService
{
    /// Ticket statistics for [fromUtc, toUtc):
    /// - Opened: tickets created in the period.
    /// - Closed: tickets whose current status is Resolved/Closed and whose
    ///   close moment (closed_utc, falling back to resolved_utc) is in the
    ///   period. A ticket that was closed and later reopened is not counted
    ///   here — it shows up under OpenNow instead.
    /// - OpenNow: snapshot of every ticket currently not Resolved/Closed,
    ///   regardless of when it was created.
    /// Soft-deleted tickets never count. Each section's item list is capped
    /// at maxItems (0 = counts only) with its own offset for paging; Count
    /// always reflects the full total. A non-null companyId narrows every
    /// section to tickets linked to that company; an unknown id simply
    /// yields empty sections (no existence probe).
    Task<TicketPeriodReport> GetPeriodReportAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxItems,
        int openedOffset,
        int closedOffset,
        int openOffset,
        Guid? companyId,
        CancellationToken ct = default);

    /// All companies (active and inactive — tickets stay linked to
    /// deactivated companies, so consumers need both), ordered by name.
    /// Same cap/offset/truncated contract as the ticket sections.
    Task<CompanyReportList> ListCompaniesAsync(
        int maxItems,
        int offset,
        CancellationToken ct = default);
}

public sealed record CompanyReportItem(Guid Id, string Name, string? Code, bool IsActive);

public sealed record CompanyReportList(
    int Count,
    IReadOnlyList<CompanyReportItem> Items,
    int Offset,
    bool Truncated);

public sealed record TicketReportItem(long Number, string Subject);

public sealed record TicketReportSection(
    int Count,
    IReadOnlyList<TicketReportItem> Items,
    int Offset,
    bool Truncated);

public sealed record TicketPeriodReport(
    TicketReportSection Opened,
    TicketReportSection Closed,
    TicketReportSection OpenNow);
