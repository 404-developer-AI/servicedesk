import { create } from "zustand";
import { preferencesApi } from "@/lib/api";
import type { OutboundMailKind } from "@/lib/ticket-api";

export type Draft = {
  ticketId: string;
  bodyHtml: string;
  isInternal: boolean;
  tab: "note" | "reply";
  updatedUtc: string;
};

// Transient (not persisted) — set when the agent clicks Reply / Reply-all /
// Forward on a specific MailReceived event so <AddNoteForm> + <SendMailForm>
// can react by expanding, switching to the mail tab and pre-filling from the
// clicked event instead of the ticket's latest inbound.
export type PendingMailAction = {
  id: number;  // monotonic — lets listeners detect a fresh click on the same event
  ticketId: string;
  kind: OutboundMailKind;
  source: {
    // `from` is the *display sender* for the quote preamble. For inbound it's
    // the external sender; for outbound it's our own mailbox so the "On X,
    // Y wrote:" line still makes sense.
    from: { address: string; name: string } | null;
    to: { address: string; name: string }[];
    cc: { address: string; name: string }[];
    subject: string | null;
    bodyHtml: string | null;
    receivedUtc: string | null;
    // When true the event is our own outbound mail — a "Reply" then targets
    // the original audience (source.to) rather than source.from, because
    // replying *to ourselves* is never what the agent means.
    isOutbound: boolean;
  };
};

type WorkspaceState = {
  lastTicketId: string | null;
  sidebarCollapsed: boolean;
  ticketSidePanelPinned: boolean;
  drafts: Record<string, Draft>;
  loaded: boolean;
  pendingMailAction: PendingMailAction | null;

  setLastTicket: (ticketId: string) => void;
  setSidebarCollapsed: (collapsed: boolean) => void;
  setTicketSidePanelPinned: (pinned: boolean) => void;
  setDraft: (
    ticketId: string,
    draft: Pick<Draft, "bodyHtml" | "isInternal" | "tab">,
  ) => void;
  removeDraft: (ticketId: string) => void;
  getDraft: (ticketId: string) => Draft | undefined;
  requestMailAction: (
    intent: Omit<PendingMailAction, "id">,
  ) => void;
  clearMailAction: () => void;
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
  entries.push({
    key: "workspace:ticketSidePanelPinned",
    value: String(state.ticketSidePanelPinned),
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
    ticketSidePanelPinned: boolean;
    drafts: Record<string, Draft>;
  } = {
    lastTicketId: null,
    sidebarCollapsed: false,
    ticketSidePanelPinned: false,
    drafts: {},
  };

  for (const [key, value] of Object.entries(entries)) {
    if (key === "workspace:lastTicket") {
      result.lastTicketId = value;
    } else if (key === "workspace:sidebar") {
      result.sidebarCollapsed = value === "true";
    } else if (key === "workspace:ticketSidePanelPinned") {
      result.ticketSidePanelPinned = value === "true";
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

let mailActionCounter = 0;

export const useWorkspaceStore = create<WorkspaceState>((set, get) => ({
  lastTicketId: null,
  sidebarCollapsed: false,
  ticketSidePanelPinned: false,
  drafts: {},
  loaded: false,
  pendingMailAction: null,

  setLastTicket: (ticketId) => set({ lastTicketId: ticketId }),

  setSidebarCollapsed: (collapsed) => set({ sidebarCollapsed: collapsed }),

  setTicketSidePanelPinned: (pinned) => {
    set({ ticketSidePanelPinned: pinned });
    // Persist immediately — pin/unpin is a deliberate user action and the
    // expectation is that the next ticket-open already respects the change,
    // even on a different tab. Other workspace fields rely on the periodic
    // auto-save in useWorkspaceAutoSave.
    preferencesApi.fireAndForgetWorkspaceSave([
      { key: "workspace:ticketSidePanelPinned", value: String(pinned) },
    ]);
  },

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

  requestMailAction: (intent) =>
    set({
      pendingMailAction: { ...intent, id: ++mailActionCounter },
    }),

  clearMailAction: () => set({ pendingMailAction: null }),

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
