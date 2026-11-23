import { create } from "zustand";
import { preferencesApi } from "@/lib/preferences-api";
import type { FormDraft } from "@/lib/formDraft";
import type {
  MailRecipientInput,
  OutboundMailKind,
  TicketAttachmentMeta,
} from "@/lib/ticket-api";

export type Draft = {
  ticketId: string;
  bodyHtml: string;
  isInternal: boolean;
  tab: "note" | "reply";
  updatedUtc: string;
};

// In-progress outbound mail on the ticket's "Send mail" tab. Kept in a slot
// separate from the note/reply `Draft` so the two never overwrite each other —
// an agent can have both a half-written note and a half-written mail on the
// same ticket. The `bodyHtml` already carries the folded-in signature marker +
// quote exactly as the editor serialises them, so it is restored verbatim and
// the signature re-injection is suppressed for a restored draft.
export type MailDraft = {
  ticketId: string;
  kind: OutboundMailKind;
  to: MailRecipientInput[];
  cc: MailRecipientInput[];
  bcc: MailRecipientInput[];
  showCc: boolean;
  showBcc: boolean;
  subject: string;
  bodyHtml: string;
  // True when a signature preview was folded into bodyHtml at compose time, so
  // the send call still asks the server to swap the marker for the real
  // signature rather than appending at the bottom.
  signaturePreloaded: boolean;
  // The id of the feed Reply/Reply-all/Forward action this draft was started
  // from (null for a plain "New" compose). Lets the composer tell a remount of
  // the same action — where the in-progress draft must be restored — from a
  // genuinely new feed click, which deliberately overrides the draft.
  intentId: number | null;
  // Completed uploads (images pasted into the body, files in the tray) so a
  // remount can hydrate the attachment list — the body alone keeps the image
  // URLs but not the ids the send payload needs. Optional: drafts saved
  // before v0.1.6 have none, and the server also resolves body URLs itself.
  attachments?: TicketAttachmentMeta[];
  updatedUtc: string;
};

// v0.1.17 — in-progress phone-call log on the composer's "Call" button. Its
// own slot so a half-written note, mail and call never overwrite each other.
export type CallDraft = {
  ticketId: string;
  bodyHtml: string;
  isInternal: boolean;
  updatedUtc: string;
};

// v0.1.55 — an in-progress ticket form (fill dialog). Persisted like the
// composer drafts, so a form survives switching tickets, a reload or a crash
// and can be minimised to the tray while the agent copies text elsewhere.
// One slot per ticket + template (fresh fill) or ticket + form (edit).
export type FormDraftEntry = {
  key: string;
  ticketId: string;
  ticketNumber: number | null;
  formName: string;
  mode: "create" | "edit";
  /** create: the template; edit: the saved form and the version edited. */
  templateId?: string;
  formId?: string;
  version?: number;
  draft: FormDraft;
  /** JSON of the answers the dialog started from — tells "changed" apart. */
  baseline: string;
  minimized: boolean;
  updatedUtc: string;
};

export function formDraftKey(
  target: { ticketId: string } & ({ mode: "create"; templateId: string } | { mode: "edit"; formId: string }),
): string {
  return target.mode === "create"
    ? `${target.ticketId}:create:${target.templateId}`
    : `${target.ticketId}:edit:${target.formId}`;
}

const FORM_DRAFT_PREFIX = "workspace:formdraft:";

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
  mailDrafts: Record<string, MailDraft>;
  callDrafts: Record<string, CallDraft>;
  formDrafts: Record<string, FormDraftEntry>;
  loaded: boolean;
  pendingMailAction: PendingMailAction | null;
  /** Transient: the form draft the tray asked the ticket page to reopen. */
  pendingFormRestore: string | null;

  setLastTicket: (ticketId: string) => void;
  setSidebarCollapsed: (collapsed: boolean) => void;
  setTicketSidePanelPinned: (pinned: boolean) => void;
  setDraft: (
    ticketId: string,
    draft: Pick<Draft, "bodyHtml" | "isInternal" | "tab">,
  ) => void;
  removeDraft: (ticketId: string) => void;
  getDraft: (ticketId: string) => Draft | undefined;
  setMailDraft: (
    ticketId: string,
    draft: Omit<MailDraft, "ticketId" | "updatedUtc">,
  ) => void;
  removeMailDraft: (ticketId: string) => void;
  getMailDraft: (ticketId: string) => MailDraft | undefined;
  setCallDraft: (ticketId: string, draft: Pick<CallDraft, "bodyHtml" | "isInternal">) => void;
  removeCallDraft: (ticketId: string) => void;
  getCallDraft: (ticketId: string) => CallDraft | undefined;
  setFormDraft: (entry: Omit<FormDraftEntry, "updatedUtc">) => void;
  setFormDraftMinimized: (key: string, minimized: boolean) => void;
  /** Drops the draft here and on the server (sent, discarded). */
  removeFormDraft: (key: string) => void;
  requestFormRestore: (key: string) => void;
  clearFormRestore: () => void;
  requestMailAction: (
    intent: Omit<PendingMailAction, "id">,
  ) => void;
  clearMailAction: () => void;
  /** Resolves with the raw server map (null on failure) so the caller can
   *  seed the shared ["preferences", "workspace"] query cache with it. */
  loadFromServer: () => Promise<Record<string, string> | null>;
  flush: () => Promise<void>;
  flushSync: () => void;
};

// v0.1.4 — signature of the last entry set sent to the server. The
// auto-save flushes on every route change; before this it PUT the full
// entry set (all drafts included) every time, whether or not anything had
// changed — one redundant write per ticket open, which added up under the
// per-session rate-limit budget. An unchanged set is now a no-op.
// v0.1.25 — and only the entries whose value changed since the last save are
// sent: the full set (every open draft) was up to 154 rows per save in the
// Performance monitor, for usually one changed key.
const lastSaved = new Map<string, string>();

function changedEntries(entries: Array<{ key: string; value: string }>) {
  return entries.filter((e) => lastSaved.get(e.key) !== e.value);
}

function markSaved(entries: Array<{ key: string; value: string }>) {
  for (const e of entries) lastSaved.set(e.key, e.value);
}

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
  for (const mailDraft of Object.values(state.mailDrafts)) {
    entries.push({
      key: `workspace:maildraft:${mailDraft.ticketId}`,
      value: JSON.stringify(mailDraft),
    });
  }
  for (const callDraft of Object.values(state.callDrafts)) {
    entries.push({
      key: `workspace:calldraft:${callDraft.ticketId}`,
      value: JSON.stringify(callDraft),
    });
  }
  for (const formDraft of Object.values(state.formDrafts)) {
    // `minimized` is per window, not state worth a server write: a loaded
    // draft always comes back minimised (see fromEntries).
    const stored: Partial<FormDraftEntry> = { ...formDraft };
    delete stored.minimized;
    entries.push({ key: FORM_DRAFT_PREFIX + formDraft.key, value: JSON.stringify(stored) });
  }
  return entries;
}

function fromEntries(entries: Record<string, string>) {
  const result: {
    lastTicketId: string | null;
    sidebarCollapsed: boolean;
    ticketSidePanelPinned: boolean;
    drafts: Record<string, Draft>;
    mailDrafts: Record<string, MailDraft>;
    callDrafts: Record<string, CallDraft>;
    formDrafts: Record<string, FormDraftEntry>;
  } = {
    lastTicketId: null,
    sidebarCollapsed: false,
    ticketSidePanelPinned: false,
    drafts: {},
    mailDrafts: {},
    callDrafts: {},
    formDrafts: {},
  };

  for (const [key, value] of Object.entries(entries)) {
    if (key === "workspace:lastTicket") {
      result.lastTicketId = value;
    } else if (key === "workspace:sidebar") {
      result.sidebarCollapsed = value === "true";
    } else if (key === "workspace:ticketSidePanelPinned") {
      result.ticketSidePanelPinned = value === "true";
    } else if (key.startsWith(FORM_DRAFT_PREFIX)) {
      try {
        const formDraft = JSON.parse(value) as FormDraftEntry;
        if (formDraft?.key && formDraft.ticketId && formDraft.draft) {
          // A draft found at load time was open in a window that went away:
          // it lands in the tray, never as an open dialog.
          result.formDrafts[formDraft.key] = { ...formDraft, minimized: true };
        }
      } catch {
        // ignore corrupt form-draft entries
      }
    } else if (key.startsWith("workspace:calldraft:")) {
      try {
        const callDraft = JSON.parse(value) as CallDraft;
        result.callDrafts[callDraft.ticketId] = callDraft;
      } catch {
        // ignore corrupt call-draft entries
      }
    } else if (key.startsWith("workspace:maildraft:")) {
      try {
        const mailDraft = JSON.parse(value) as MailDraft;
        result.mailDrafts[mailDraft.ticketId] = mailDraft;
      } catch {
        // ignore corrupt mail-draft entries
      }
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
  mailDrafts: {},
  callDrafts: {},
  formDrafts: {},
  loaded: false,
  pendingMailAction: null,
  pendingFormRestore: null,

  setLastTicket: (ticketId) => set({ lastTicketId: ticketId }),

  setSidebarCollapsed: (collapsed) => set({ sidebarCollapsed: collapsed }),

  setTicketSidePanelPinned: (pinned) => {
    set({ ticketSidePanelPinned: pinned });
    // Persist immediately — pin/unpin is a deliberate user action and the
    // expectation is that the next ticket-open already respects the change,
    // even on a different tab. Other workspace fields rely on the periodic
    // auto-save in useWorkspaceAutoSave.
    const entry = { key: "workspace:ticketSidePanelPinned", value: String(pinned) };
    markSaved([entry]);
    preferencesApi.fireAndForgetWorkspaceSave([entry]);
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

  setMailDraft: (ticketId, draft) =>
    set((s) => ({
      mailDrafts: {
        ...s.mailDrafts,
        [ticketId]: {
          ticketId,
          ...draft,
          updatedUtc: new Date().toISOString(),
        },
      },
    })),

  removeMailDraft: (ticketId) =>
    set((s) => {
      if (!s.mailDrafts[ticketId]) return s; // nothing to remove — no churn
      const { [ticketId]: _, ...rest } = s.mailDrafts;
      return { mailDrafts: rest };
    }),

  getMailDraft: (ticketId) => get().mailDrafts[ticketId],

  setCallDraft: (ticketId, { bodyHtml, isInternal }) =>
    set((s) => ({
      callDrafts: {
        ...s.callDrafts,
        [ticketId]: { ticketId, bodyHtml, isInternal, updatedUtc: new Date().toISOString() },
      },
    })),

  removeCallDraft: (ticketId) =>
    set((s) => {
      if (!s.callDrafts[ticketId]) return s;
      const { [ticketId]: _, ...rest } = s.callDrafts;
      return { callDrafts: rest };
    }),

  getCallDraft: (ticketId) => get().callDrafts[ticketId],

  setFormDraft: (entry) =>
    set((s) => ({
      formDrafts: { ...s.formDrafts, [entry.key]: { ...entry, updatedUtc: new Date().toISOString() } },
    })),

  setFormDraftMinimized: (key, minimized) =>
    set((s) => {
      const cur = s.formDrafts[key];
      if (!cur || cur.minimized === minimized) return s;
      return { formDrafts: { ...s.formDrafts, [key]: { ...cur, minimized } } };
    }),

  removeFormDraft: (key) => {
    if (!get().formDrafts[key]) return;
    set((s) => {
      const rest = { ...s.formDrafts };
      delete rest[key];
      return { formDrafts: rest, pendingFormRestore: s.pendingFormRestore === key ? null : s.pendingFormRestore };
    });
    const serverKey = FORM_DRAFT_PREFIX + key;
    lastSaved.delete(serverKey);
    void preferencesApi.deleteWorkspaceKey(serverKey).catch(() => {
      // best effort — a leftover key reappears in the tray on next load and
      // can be discarded there
    });
  },

  requestFormRestore: (key) => set({ pendingFormRestore: key }),
  clearFormRestore: () => set({ pendingFormRestore: null }),

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
      // What we just loaded *is* the server state — the first flush after
      // load must not re-send it unchanged.
      lastSaved.clear();
      markSaved(toEntries(get()));
      return raw;
    } catch {
      set({ loaded: true });
      return null;
    }
  },

  flush: async () => {
    const entries = changedEntries(toEntries(get()));
    if (entries.length === 0) return;
    try {
      await preferencesApi.saveWorkspace(entries);
      markSaved(entries);
    } catch {
      // silent — best-effort persistence
    }
  },

  flushSync: () => {
    const entries = changedEntries(toEntries(get()));
    if (entries.length === 0) return;
    // Optimistic: keepalive saves can't be awaited on unload. A lost save
    // is re-sent on the next change anyway.
    markSaved(entries);
    preferencesApi.fireAndForgetWorkspaceSave(entries);
  },
}));
