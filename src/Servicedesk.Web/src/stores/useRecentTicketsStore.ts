import { create } from "zustand";
import { persist } from "zustand/middleware";

const MAX_RECENT = 10;

export type RecentTicket = {
  id: string;
  number: number;
  subject: string;
};

type RecentTicketsState = {
  recentTickets: RecentTicket[];
  /** Adds a ticket. If it already exists, keeps it in its current position. New tickets go to the end. */
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
            // Update subject in place (may have changed), keep position
            return {
              recentTickets: s.recentTickets.map((t) =>
                t.id === ticket.id ? { ...t, subject: ticket.subject } : t,
              ),
            };
          }
          // New ticket goes to the end
          return {
            recentTickets: [...s.recentTickets, ticket].slice(0, MAX_RECENT),
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
