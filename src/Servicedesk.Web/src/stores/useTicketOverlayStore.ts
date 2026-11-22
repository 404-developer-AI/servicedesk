import { create } from "zustand";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";

/// v0.1.61 — the ticket overlay: a ticket opened on top of the Back office
/// forms list (or a locked tab of a queue view) so back office works without
/// leaving the list. Window-local and never persisted (like `minimized` on
/// form drafts): a reload starts clean.
///
/// `source` = where it was opened (path + query + view tab), so restoring a
/// minimised overlay brings the agent back to the list or tab they were
/// working in before the overlay re-opens.

export interface TicketOverlaySource {
  /** Pathname, e.g. "/backoffice-forms" or "/tickets". */
  path: string;
  /** Query string including "?" (keeps ?viewId=…), or "". */
  search: string;
  /** View tab id (category id or "other") when opened from a view tab. */
  tab: string | null;
}

export interface TicketOverlayEntry {
  ticketId: string;
  ticketNumber: number | null;
  subject: string | null;
  source: TicketOverlaySource | null;
}

interface TicketOverlayState {
  current: TicketOverlayEntry | null;
  minimized: TicketOverlayEntry[];
  /** Set by restore: the view page re-selects this tab on that path, then clears it. */
  pendingTab: { path: string; search: string; tab: string } | null;
  open: (entry: TicketOverlayEntry) => void;
  /** Put the open overlay in the tray. */
  minimize: () => void;
  /** Re-open a tray entry (the caller navigates to its source first). */
  restore: (ticketId: string) => void;
  close: () => void;
  discard: (ticketId: string) => void;
  clearPendingTab: () => void;
}

export const useTicketOverlayStore = create<TicketOverlayState>((set, get) => ({
  current: null,
  minimized: [],
  pendingTab: null,
  open: (entry) => {
    const { current, minimized } = get();
    // Opening another ticket while one is open keeps the first in the tray.
    const keep = current && current.ticketId !== entry.ticketId ? [current] : [];
    set({
      current: entry,
      minimized: [...keep, ...minimized.filter((m) => m.ticketId !== entry.ticketId && !keep.some((k) => k.ticketId === m.ticketId))],
    });
  },
  minimize: () => {
    const { current, minimized } = get();
    if (!current) return;
    set({ current: null, minimized: [current, ...minimized.filter((m) => m.ticketId !== current.ticketId)] });
  },
  restore: (ticketId) => {
    const { minimized, current } = get();
    const entry = minimized.find((m) => m.ticketId === ticketId);
    if (!entry) return;
    const rest = minimized.filter((m) => m.ticketId !== ticketId);
    set({
      current: entry,
      minimized: current ? [current, ...rest] : rest,
      pendingTab: entry.source?.tab ? { path: entry.source.path, search: entry.source.search, tab: entry.source.tab } : null,
    });
  },
  close: () => set({ current: null }),
  discard: (ticketId) => set({ minimized: get().minimized.filter((m) => m.ticketId !== ticketId) }),
  clearPendingTab: () => set({ pendingTab: null }),
}));

/// Back-office work on a ticket is done (its form marked as handled, or the
/// request planned): close its overlay, drop it from the tray and from the
/// sidebar "Recent" list, so the agent is back on the list with nothing left
/// to tidy up.
export function finishOverlayTicket(ticketId: string) {
  const overlay = useTicketOverlayStore.getState();
  if (overlay.current?.ticketId === ticketId) overlay.close();
  overlay.discard(ticketId);
  useRecentTicketsStore.getState().removeTicket(ticketId);
}
