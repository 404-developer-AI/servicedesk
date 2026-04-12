import { create } from "zustand";

export type PresenceUser = {
  userId: string;
  email: string;
  /** "viewing" = has the ticket open right now, "recent" = in their recent list */
  status: "viewing" | "recent";
};

type PresenceState = {
  /** ticketId → list of users with presence on that ticket */
  byTicket: Record<string, PresenceUser[]>;
  setTicketPresence: (ticketId: string, users: PresenceUser[]) => void;
  setFullSync: (data: Record<string, PresenceUser[]>) => void;
};

export const usePresenceStore = create<PresenceState>((set) => ({
  byTicket: {},
  setTicketPresence: (ticketId, users) =>
    set((s) => ({
      byTicket: {
        ...s.byTicket,
        [ticketId]: users,
      },
    })),
  setFullSync: (data) => set({ byTicket: data }),
}));
