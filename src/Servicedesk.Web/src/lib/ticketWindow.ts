// One reusable browser window for "open this ticket" links outside the SPA's
// own navigation (e.g. the timesheet / Adsolut tab). Every call targets the
// same fixed window name, so the first click opens a new window and every
// later click navigates that *same* window to the new ticket instead of
// stacking up tabs. If the user closed it, the next call simply reopens it.
const TICKET_WINDOW_NAME = "servicedesk-ticket";

/// Open (or refresh) the shared ticket window on the given ticket id and bring
/// it to the front. No-op when there is no id to open.
export function openTicketInSharedWindow(ticketId: string): void {
  if (!ticketId) return;
  const win = window.open(`/tickets/${encodeURIComponent(ticketId)}`, TICKET_WINDOW_NAME);
  // Pop-up blockers can return null; nothing to focus then.
  win?.focus();
}
