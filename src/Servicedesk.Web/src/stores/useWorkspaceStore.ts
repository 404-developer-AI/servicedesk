import { create } from "zustand";
import { preferencesApi } from "@/lib/api";

export type Draft = {
  ticketId: string;
  bodyHtml: string;
  isInternal: boolean;
  tab: "note" | "reply";
  updatedUtc: string;
};

type WorkspaceState = {
  lastTicketId: string | null;
  sidebarCollapsed: boolean;
  drafts: Record<string, Draft>;
  loaded: boolean;

  setLastTicket: (ticketId: string) => void;
  setSidebarCollapsed: (collapsed: boolean) => void;
  setDraft: (
    ticketId: string,
    draft: Pick<Draft, "bodyHtml" | "isInternal" | "tab">,
  ) => void;
  removeDraft: (ticketId: string) => void;
  getDraft: (ticketId: string) => Draft | undefined;
  loadFromServer: () => Promise<void>;
  flush: () => Promise<void>;
  flushSync: () => void;
};

function toEntries(state: WorkspaceState) {
  const entries: Array<{ key: string; value: string }> = [];
  if (state.lastTicketId) {
    entries.push({ key: "workspace:lastTicket", value: state.lastTicketId });
  }
  entries.push({
    key: "workspace:sidebar",
    value: String(state.sidebarCollapsed),
  });
  for (const draft of Object.values(state.drafts)) {
    entries.push({
      key: `workspace:draft:${draft.ticketId}`,
      value: JSON.stringify(draft),
    });
  }
  return entries;
}

function fromEntries(entries: Record<string, string>) {
  const result: {
    lastTicketId: string | null;
    sidebarCollapsed: boolean;
    drafts: Record<string, Draft>;
  } = { lastTicketId: null, sidebarCollapsed: false, drafts: {} };

  for (const [key, value] of Object.entries(entries)) {
    if (key === "workspace:lastTicket") {
      result.lastTicketId = value;
    } else if (key === "workspace:sidebar") {
      result.sidebarCollapsed = value === "true";
    } else if (key.startsWith("workspace:draft:")) {
      try {
        const draft = JSON.parse(value) as Draft;
        result.drafts[draft.ticketId] = draft;
      } catch {
        // ignore corrupt draft entries
      }
    }
  }
  return result;
}

export const useWorkspaceStore = create<WorkspaceState>((set, get) => ({
  lastTicketId: null,
  sidebarCollapsed: false,
  drafts: {},
  loaded: false,

  setLastTicket: (ticketId) => set({ lastTicketId: ticketId }),

  setSidebarCollapsed: (collapsed) => set({ sidebarCollapsed: collapsed }),

  setDraft: (ticketId, { bodyHtml, isInternal, tab }) =>
    set((s) => ({
      drafts: {
        ...s.drafts,
        [ticketId]: {
          ticketId,
          bodyHtml,
          isInternal,
          tab,
          updatedUtc: new Date().toISOString(),
        },
      },
    })),

  removeDraft: (ticketId) =>
    set((s) => {
      const { [ticketId]: _, ...rest } = s.drafts;
      return { drafts: rest };
    }),

  getDraft: (ticketId) => get().drafts[ticketId],

  loadFromServer: async () => {
    try {
      const raw = await preferencesApi.getWorkspace();
      const parsed = fromEntries(raw);
      set({ ...parsed, loaded: true });
    } catch {
      set({ loaded: true });
    }
  },

  flush: async () => {
    const entries = toEntries(get());
    if (entries.length === 0) return;
    try {
      await preferencesApi.saveWorkspace(entries);
    } catch {
      // silent — best-effort persistence
    }
  },

  flushSync: () => {
    const entries = toEntries(get());
    if (entries.length === 0) return;
    preferencesApi.fireAndForgetWorkspaceSave(entries);
  },
}));
