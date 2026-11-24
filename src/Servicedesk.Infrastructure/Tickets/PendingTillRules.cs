namespace Servicedesk.Infrastructure.Tickets;

/// v0.1.44 — the one rule every pending-till writer shares: a new value must
/// lie in the future, measured on the server clock. A past value is never a
/// real reminder: the trigger scheduler treats a boundary as fired once a
/// reminder run exists at or after it, so a pending-till set before the last
/// run on that ticket was never picked up again and the ticket stayed in
/// Pending for good (Ticket#19964).
///
/// Writers: side-panel PATCH + bulk (`TicketMutationService` precheck),
/// ticket create (endpoint), trigger `set_pending_till` (handler → failed
/// action), Zammad import (elapsed value → left empty).
///
/// Second rule, same writers except the fixed-value trigger action: a new
/// value needs a Pending status (<see cref="NotPendingCode"/>). The field
/// writer also only writes the column when the effective status is Pending,
/// so a value sent with a move out of Pending is dropped, not refused.
public static class PendingTillRules
{
    public const string PastCode = "pending_till_in_past";
    public const string PastMessage = "Pending till must be in the future.";

    /// A pending-till only ever lives on a Pending-category ticket (v0.0.100:
    /// a leaked value made the scheduler re-evaluate the ticket every tick,
    /// and a chained reminder fires whatever the status).
    public const string NotPendingCode = "pending_till_not_pending";
    public const string NotPendingMessage = "Pending till can only be set on a ticket in a Pending status.";

    /// True when <paramref name="valueUtc"/> is set and not after
    /// <paramref name="nowUtc"/>. A null value (no reminder) is never past.
    public static bool IsInPast(DateTime? valueUtc, DateTime nowUtc)
        => valueUtc.HasValue && ToUtc(valueUtc)!.Value <= ToUtc(nowUtc)!.Value;

    /// Same moment in time, regardless of DateTimeKind (Dapper hands back
    /// Unspecified for timestamptz in some paths, JSON binding gives Utc).
    public static bool SameInstant(DateTime? a, DateTime? b)
        => a.HasValue == b.HasValue && (!a.HasValue || ToUtc(a)!.Value == ToUtc(b)!.Value);

    /// Normalises to a Utc-kind value. Unspecified is taken as UTC — every
    /// pending-till column and API field is UTC by contract.
    public static DateTime? ToUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };
}
