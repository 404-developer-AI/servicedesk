namespace Servicedesk.Infrastructure.Timesheet;

/// v0.0.87 — per-ticket hour-limit alert. Snapshot of where a ticket sits
/// relative to its effective time limit, plus the dialog defaults the UI
/// needs to render the "allow more time" form. All values are server-derived;
/// the client never computes the limit or the exceeded flag itself.
public sealed record TicketTimeAlertStatus(
    bool Enabled,
    int ThresholdMinutes,       // global default (Timesheet.TimeAlertThresholdMinutes)
    int ExtraMinutes,           // cumulative grant on this ticket
    int LimitMinutes,           // effective limit = threshold + extra
    int TotalMinutes,           // all logged time on the ticket (all agents)
    int RemainingMinutes,       // limit - total (negative once over)
    bool Exceeded,              // enabled && total > limit
    int DefaultExtraMinutes,    // "allow more time" dialog pre-fill
    string ConfirmationText);   // mandatory-tick label

/// Outcome of an attempt to raise a ticket's hour limit.
public enum TicketTimeAlertExtendResult
{
    Ok,
    Disabled,        // the feature master-switch is off
    NotConfirmed,    // the mandatory customer-confirmation tick was not set
    InvalidMinutes,  // added minutes <= 0 or above the sanity cap
    TicketNotFound,
}

public interface ITicketTimeAlertService
{
    /// Computes the live limit/total snapshot for a ticket. Used by the
    /// ticket-detail popup (to decide whether to warn) and the "Time logged"
    /// panel (to show remaining time).
    Task<TicketTimeAlertStatus> GetStatusAsync(Guid ticketId, CancellationToken ct = default);

    /// Logs that the agent dismissed the alert. The limit is unchanged, so
    /// the warning recurs the next time the ticket is opened while over limit.
    Task DismissAsync(Guid ticketId, Guid actorUserId, CancellationToken ct = default);

    /// Raises the ticket's limit by <paramref name="addMinutes"/>. Requires the
    /// written-customer-confirmation tick; logs a TimeLimitExtended event.
    Task<TicketTimeAlertExtendResult> ExtendAsync(
        Guid ticketId, Guid actorUserId, int addMinutes, bool customerConfirmed,
        CancellationToken ct = default);
}
