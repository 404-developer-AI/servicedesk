import { request } from "@/lib/http";
import type { FormAnswers, FormField } from "@/lib/forms-api";

/// v0.1.54 — Back office forms: every form filled from a template marked
/// "Send to back office" lands on this work list. Gated server-side by the
/// per-user `backoffice_forms_enabled` flag (403 when off).
/// v0.1.61 — Open → Handled (no claim step). Who is working on a form is
/// live presence (`presence`), never stored. An open form whose ticket waits
/// for the customer (setting-driven) is `waiting` and never counts.

export type BackofficeFormStatus = "Open" | "Handled";
export type BackofficeFormTab = BackofficeFormStatus | "All";

export interface BackofficeFormItem {
  id: string;
  ticketId: string;
  ticketNumber: number;
  ticketSubject: string;
  companyId: string | null;
  companyName: string | null;
  templateId: string | null;
  name: string;
  version: number;
  status: BackofficeFormStatus;
  submittedByUserId: string | null;
  submittedByName: string | null;
  submittedUtc: string;
  updatedByName: string | null;
  updatedUtc: string;
  handledByUserId: string | null;
  handledByName: string | null;
  handledUtc: string | null;
  handledVersion: number | null;
  handledNote: string;
  /** Changed after it was handled — back on the list. */
  reopened: boolean;
  ticketStatusName: string;
  ticketStateCategory: string;
  ticketPendingTillUtc: string | null;
  /** Open form whose ticket waits for the customer (never counted). */
  waiting: boolean;
  /** The template's "Plan with form": the form "Plan" opens, or null. */
  planTemplateId: string | null;
}

export interface BackofficeFormLogEntry {
  id: number;
  /** claimed / released only on history from before v0.1.61. */
  action: "submitted" | "claimed" | "released" | "handled" | "reopened" | "updated";
  userId: string | null;
  userName: string | null;
  note: string;
  formVersion: number;
  createdUtc: string;
}

export interface BackofficeFormChange {
  fieldId: string;
  label: string;
  before: string;
  after: string;
}

export interface BackofficeFormDetail {
  item: BackofficeFormItem;
  fields: FormField[];
  answers: FormAnswers;
  log: BackofficeFormLogEntry[];
  changesSinceHandled: BackofficeFormChange[];
}

export interface BackofficeFormListParams {
  status: BackofficeFormTab;
  templateId?: string | null;
  companyId?: string | null;
  submittedBy?: string | null;
  q?: string;
  offset: number;
  limit: number;
}

export interface BackofficePresenceUser {
  userId: string;
  name: string;
}

/** ticketId → back-office users who have that ticket open right now. */
export type BackofficePresenceMap = Record<string, BackofficePresenceUser[]>;

export const BACKOFFICE_FORMS_KEY = ["backoffice-forms"] as const;
export const backofficeFormKeys = {
  list: (p: BackofficeFormListParams) => [...BACKOFFICE_FORMS_KEY, "list", p] as const,
  counts: () => [...BACKOFFICE_FORMS_KEY, "counts"] as const,
  templates: () => [...BACKOFFICE_FORMS_KEY, "templates"] as const,
  detail: (id: string) => [...BACKOFFICE_FORMS_KEY, "detail", id] as const,
  onTicket: (ticketId: string) => [...BACKOFFICE_FORMS_KEY, "ticket", ticketId] as const,
  /** Not under BACKOFFICE_FORMS_KEY on purpose: presence pings refetch only this. */
  presence: () => ["backoffice-presence"] as const,
};

const enc = encodeURIComponent;

function qs(p: BackofficeFormListParams): string {
  const s = new URLSearchParams();
  if (p.status !== "All") s.set("status", p.status);
  if (p.templateId) s.set("templateId", p.templateId);
  if (p.companyId) s.set("companyId", p.companyId);
  if (p.submittedBy) s.set("submittedBy", p.submittedBy);
  if (p.q?.trim()) s.set("q", p.q.trim());
  s.set("offset", String(p.offset));
  s.set("limit", String(p.limit));
  return s.toString();
}

export const backofficeFormsApi = {
  list: (p: BackofficeFormListParams) =>
    request<{ items: BackofficeFormItem[]; total: number }>("GET", `/api/backoffice-forms/?${qs(p)}`),
  /** ready = waiting for pickup (badge, red dot); waiting = ticket waits for the customer. */
  counts: () => request<{ ready: number; waiting: number }>("GET", "/api/backoffice-forms/counts"),
  templates: () =>
    request<{ items: { id: string; name: string; isActive: boolean; planTemplateId: string | null }[] }>(
      "GET", "/api/backoffice-forms/templates",
    ).then((r) => r.items),
  get: (id: string) => request<BackofficeFormDetail>("GET", `/api/backoffice-forms/${enc(id)}`),
  onTicket: (ticketId: string) =>
    request<{ items: BackofficeFormItem[] }>("GET", `/api/backoffice-forms/ticket/${enc(ticketId)}`).then((r) => r.items),
  presence: () =>
    request<{ items: { ticketId: string; users: BackofficePresenceUser[] }[] }>("GET", "/api/backoffice-forms/presence")
      .then((r) => Object.fromEntries(r.items.map((i) => [i.ticketId, i.users])) as BackofficePresenceMap),
  handle: (id: string, note: string, expectedVersion: number) =>
    request<BackofficeFormItem>("POST", `/api/backoffice-forms/${enc(id)}/handle`, { note, expectedVersion }),
  reopen: (id: string, note: string) =>
    request<BackofficeFormItem>("POST", `/api/backoffice-forms/${enc(id)}/reopen`, { note }),
};
