import { create } from "zustand";
import { persist } from "zustand/middleware";

// Upper bound so the persisted list in localStorage doesn't grow
// unbounded. The sidebar list itself is scrollable, so a larger cap is
// fine — most agents will never hit this.
const MAX_RECENT = 50;

export type RecentTicket = {
  id: string;
  number: number;
  subject: string;
  // v0.0.37 — surfaced in the sidebar as a status-colored bar + icon
  // tint so closed / WFQ / QFI rows stand out without re-opening.
  // Optional because pre-v0.0.37 persisted entries don't have them;
  // they re-populate the first time the ticket is viewed.
  statusColor?: string;
  statusName?: string;
  statusStateCategory?: string;
};

type RecentTicketsState = {
  recentTickets: RecentTicket[];
  /** Adds a ticket. If it already exists, refreshes the mutable fields (subject + status) in place. New tickets go to the end. */
  addTicket: (ticket: RecentTicket) => void;
  removeTicket: (id: string) => void;
  /** Move a ticket from one index to another. */
  moveTicket: (fromIndex: number, toIndex: number) => void;
  clearRecents: () => void;
};

export const useRecentTicketsStore = create<RecentTicketsState>()(
  persist(
    (set) => ({
      recentTickets: [],
      addTicket: (ticket) =>
        set((s) => {
          const exists = s.recentTickets.some((t) => t.id === ticket.id);
          if (exists) {
            // Refresh the mutable fields (subject + status snapshot) in
            // place. Position is preserved so the user's manual ordering
            // survives a re-open.
            return {
              recentTickets: s.recentTickets.map((t) =>
                t.id === ticket.id
                  ? {
                      ...t,
                      subject: ticket.subject,
                      statusColor: ticket.statusColor ?? t.statusColor,
                      statusName: ticket.statusName ?? t.statusName,
                      statusStateCategory:
                        ticket.statusStateCategory ?? t.statusStateCategory,
                    }
                  : t,
              ),
            };
          }
          // New ticket goes to the end; when we're at cap, trim the
          // OLDEST entries (head) — `slice(0, MAX_RECENT)` used to drop
          // the just-added ticket at the tail, which meant the nav
          // effectively stopped updating after the 10th open ticket.
          return {
            recentTickets: [...s.recentTickets, ticket].slice(-MAX_RECENT),
          };
        }),
      removeTicket: (id) =>
        set((s) => ({
          recentTickets: s.recentTickets.filter((t) => t.id !== id),
        })),
      moveTicket: (fromIndex, toIndex) =>
        set((s) => {
          const list = [...s.recentTickets];
          if (
            fromIndex < 0 ||
            fromIndex >= list.length ||
            toIndex < 0 ||
            toIndex >= list.length ||
            fromIndex === toIndex
          )
            return s;
          const [item] = list.splice(fromIndex, 1);
          list.splice(toIndex, 0, item);
          return { recentTickets: list };
        }),
      clearRecents: () => set({ recentTickets: [] }),
    }),
    { name: "sd-recent-tickets" },
  ),
);
