/// Frontend mirror of the backend `TicketReference` helper. The canonical
/// human-facing reference is `<prefix><number>`, e.g. "Ticket#1234". The prefix
/// is admin-configurable (Tickets.ReferencePrefix); read it via
/// `useTicketReferencePrefix()`.
///
/// Only formatting lives here: parsing a pasted reference back to a ticket is
/// done server-side (global search, ticket picker, timesheet link all hit the
/// backend), so the client never needs to strip the prefix itself.

export const DEFAULT_TICKET_REFERENCE_PREFIX = "Ticket#";

/// Formats a ticket number with the configured prefix, e.g. "Ticket#1234".
export function formatTicketRef(
  ticketNumber: number | string,
  prefix: string = DEFAULT_TICKET_REFERENCE_PREFIX,
): string {
  const p = prefix && prefix.trim().length > 0 ? prefix : DEFAULT_TICKET_REFERENCE_PREFIX;
  return `${p}${ticketNumber}`;
}
