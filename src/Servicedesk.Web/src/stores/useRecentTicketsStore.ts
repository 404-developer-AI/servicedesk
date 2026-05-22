import { create } from "zustand";
import { recentTicketsApi } from "@/lib/api";

/// v0.0.42 — recent-tickets storage moved from `localStorage` to a
/// server-side per-user list with SignalR cross-browser sync. The store
/// keeps the same callable surface as before (addTicket / removeTicket /
/// moveTicket / clearRecents) so the consuming components do not change.
/// Each mutation updates the in-memory cache immediately (optimistic)
/// and fires a fetch in the background; SignalR pushes from other tabs
/// trigger `hydrateFromServer` so every browser converges.
///
/// Upper bound matches the previous in-memory cap of 50. The server
/// enforces the same cap on insert (RecentTicketsService.MaxEntriesPerUser);
/// the local trim here is purely a UX guardrail so an optimistic add
/// renders the trimmed list immediately instead of after the round-trip.
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
  /** True after the first server-hydrate completes. Sidebar can use it
   *  to avoid flashing an empty list during the initial fetch. */
  loaded: boolean;
  /** Replaces the local cache with the supplied list. Used by the
   *  initial hydrate-from-server and the SignalR invalidation handler. */
  setFromServer: (items: RecentTicket[]) => void;
  /** Adds a ticket. If it already exists, refreshes the mutable fields
   *  (subject + status snapshot) in place. New tickets go to the end.
   *  Fires a fire-and-forget POST in the background. */
  addTicket: (ticket: RecentTicket) => void;
  removeTicket: (id: string) => void;
  /** Move a ticket from one index to another. */
  moveTicket: (fromIndex: number, toIndex: number) => void;
  clearRecents: () => void;
};

let reorderTimer: number | null = null;

function scheduleReorder(ticketIds: string[]) {
  if (reorderTimer !== null) {
    window.clearTimeout(reorderTimer);
  }
  reorderTimer = window.setTimeout(() => {
    reorderTimer = null;
    recentTicketsApi.reorder(ticketIds).catch(() => {
      // Best-effort — a failed reorder will resync on the next SignalR
      // push or page reload. We don't roll back the optimistic UI
      // because that would feel worse than a slightly stale order.
    });
  }, 300);
}

export const useRecentTicketsStore = create<RecentTicketsState>()((set, get) => ({
  recentTickets: [],
  loaded: false,
  setFromServer: (items) => set({ recentTickets: items, loaded: true }),
  addTicket: (ticket) => {
    set((s) => {
      const exists = s.recentTickets.some((t) => t.id === ticket.id);
      if (exists) {
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
      return {
        recentTickets: [...s.recentTickets, ticket].slice(-MAX_RECENT),
      };
    });
    recentTicketsApi.add(ticket.id).catch(() => {
      // Same best-effort discipline — the server is the source of truth
      // and the next hydrate will reconcile a failed write.
    });
  },
  removeTicket: (id) => {
    set((s) => ({
      recentTickets: s.recentTickets.filter((t) => t.id !== id),
    }));
    recentTicketsApi.remove(id).catch(() => {});
  },
  moveTicket: (fromIndex, toIndex) => {
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
      // Debounce so a drag-shuffle doesn't fire one PUT per pixel.
      scheduleReorder(list.map((t) => t.id));
      return { recentTickets: list };
    });
  },
  clearRecents: () => {
    const ids = get().recentTickets.map((t) => t.id);
    set({ recentTickets: [] });
    Promise.all(ids.map((id) => recentTicketsApi.remove(id).catch(() => {}))).catch(() => {});
  },
}));

/// Fetches the server-side list and replaces the local cache. Called
/// once on auth-bootstrap and again on every `RecentTicketsUpdated`
/// SignalR push so cross-tab edits converge within ~100ms.
///
/// First-call also migrates a legacy localStorage list (key
/// `sd-recent-tickets`, from before v0.0.42) when the server returns
/// empty: every legacy entry is POSTed to the server in order, then
/// the localStorage key is cleared so the next hydrate starts clean.
export async function hydrateRecentTicketsFromServer(): Promise<void> {
  try {
    const { items } = await recentTicketsApi.list();
    if (items.length === 0) {
      const migrated = await maybeMigrateLegacyLocalStorage();
      if (migrated.length > 0) {
        useRecentTicketsStore.getState().setFromServer(migrated);
        return;
      }
    }
    useRecentTicketsStore.getState().setFromServer(items);
  } catch {
    // Leave the store untouched on failure — a stale list is better
    // than an empty sidebar. Next push or reload will retry.
  }
}

const LEGACY_KEY = "sd-recent-tickets";

async function maybeMigrateLegacyLocalStorage(): Promise<RecentTicket[]> {
  let stored: string | null = null;
  try {
    stored = window.localStorage.getItem(LEGACY_KEY);
  } catch {
    return [];
  }
  if (!stored) return [];

  let legacy: RecentTicket[] = [];
  try {
    const parsed = JSON.parse(stored) as { state?: { recentTickets?: RecentTicket[] } };
    legacy = parsed?.state?.recentTickets ?? [];
  } catch {
    legacy = [];
  }
  if (!Array.isArray(legacy) || legacy.length === 0) {
    try { window.localStorage.removeItem(LEGACY_KEY); } catch { /* ignore */ }
    return [];
  }

  // POST in the legacy order so the server-side position matches the
  // user's manual ordering pre-migration. Each call is await'd because
  // the server appends at the tail and we want the same tail order.
  for (const t of legacy) {
    if (!t?.id) continue;
    try {
      await recentTicketsApi.add(t.id);
    } catch {
      // Skip and continue — a single failed migration entry should not
      // block the rest of the list.
    }
  }

  try { window.localStorage.removeItem(LEGACY_KEY); } catch { /* ignore */ }

  // Re-fetch so the local cache mirrors what actually landed on the
  // server (some entries may have been skipped because their ticket no
  // longer exists). Returned to the caller for one-shot priming.
  try {
    const { items } = await recentTicketsApi.list();
    return items;
  } catch {
    return [];
  }
}
