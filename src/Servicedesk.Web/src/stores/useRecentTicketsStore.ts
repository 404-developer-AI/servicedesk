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
  addTicket: (ticket: RecentTicket) => void;
  removeTicket: (id: string) => void;
  clearRecents: () => void;
};

export const useRecentTicketsStore = create<RecentTicketsState>()(
  persist(
    (set) => ({
      recentTickets: [],
      addTicket: (ticket) =>
        set((s) => {
          const filtered = s.recentTickets.filter((t) => t.id !== ticket.id);
          return {
            recentTickets: [ticket, ...filtered].slice(0, MAX_RECENT),
          };
        }),
      removeTicket: (id) =>
        set((s) => ({
          recentTickets: s.recentTickets.filter((t) => t.id !== id),
        })),
      clearRecents: () => set({ recentTickets: [] }),
    }),
    { name: "sd-recent-tickets" },
  ),
);
