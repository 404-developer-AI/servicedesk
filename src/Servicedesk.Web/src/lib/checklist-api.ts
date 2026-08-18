// v0.0.103 — ticket checklists: agent-side (checklists on a ticket) and
// admin-side (templates) API surface + shared types.

import { csrfHeader } from "@/lib/csrf";
import { ApiError } from "@/lib/ticket-api";

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const isSafe = method === "GET" || method === "HEAD";
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(isSafe ? {} : csrfHeader()),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let parsedBody: unknown = null;
    try {
      const txt = await res.text();
      if (txt) parsedBody = JSON.parse(txt);
    } catch {
      // non-JSON body
    }
    throw new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`, parsedBody);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** Reads the `{ error, code }` body the checklist endpoints return. */
export function checklistErrorMessage(err: unknown, fallback: string): string {
  if (err instanceof ApiError && err.body && typeof err.body === "object") {
    const b = err.body as { error?: unknown };
    if (typeof b.error === "string" && b.error.trim()) return b.error;
  }
  return fallback;
}

export function checklistErrorCode(err: unknown): string | null {
  if (err instanceof ApiError && err.body && typeof err.body === "object") {
    const b = err.body as { code?: unknown };
    if (typeof b.code === "string") return b.code;
  }
  return null;
}

// ---- settings (agent-readable) ----

export type ChecklistSettings = {
  enabled: boolean;
  /** Subset of "Resolved" | "Closed" — categories the close block applies to. */
  blockingStateCategories: string[];
  logItemChangesToTimeline: boolean;
  maxPerTicket: number;
  maxItemsPerChecklist: number;
};

// ---- ticket-side types ----

export type ChecklistItemState = "open" | "done" | "na";

export type TicketChecklistItem = {
  id: string;
  checklistId: string;
  sectionId: string | null;
  title: string;
  description: string;
  teamLabel: string;
  timingLabel: string;
  linkUrl: string;
  linkLabel: string;
  isRequired: boolean;
  sortOrder: number;
  isAdHoc: boolean;
  addedByUserId: string | null;
  addedByName: string | null;
  state: ChecklistItemState;
  stateChangedUtc: string | null;
  stateChangedByUserId: string | null;
  stateChangedByName: string | null;
  naReason: string;
  commentCount: number;
  createdUtc: string;
};

export type TicketChecklistSection = {
  id: string;
  title: string;
  sortOrder: number;
};

export type TicketChecklist = {
  id: string;
  ticketId: string;
  templateId: string | null;
  name: string;
  description: string;
  blockClose: boolean;
  sortOrder: number;
  attachedByUserId: string | null;
  attachedByName: string | null;
  attachedUtc: string;
  completedUtc: string | null;
  requiredTotal: number;
  requiredDone: number;
  totalItems: number;
  doneItems: number;
  touched: boolean;
  sections: TicketChecklistSection[];
  items: TicketChecklistItem[];
};

export type ChecklistItemEvent = {
  id: number;
  kind: "state_change" | "comment" | "item_added" | "item_edited" | "item_removed" | (string & {});
  userId: string | null;
  userName: string | null;
  fromState: ChecklistItemState | null;
  toState: ChecklistItemState | null;
  comment: string;
  createdUtc: string;
};

export type AvailableChecklistTemplate = {
  id: string;
  name: string;
  description: string;
  itemCount: number;
  blockClose: boolean;
};

export type ChecklistItemInput = {
  sectionId?: string | null;
  title: string;
  description?: string;
  teamLabel?: string;
  timingLabel?: string;
  linkUrl?: string;
  linkLabel?: string;
  isRequired?: boolean;
};

export const ticketChecklistApi = {
  settings: () => request<ChecklistSettings>("GET", "/api/settings/checklists"),
  list: (ticketId: string) =>
    request<{ items: TicketChecklist[] }>("GET", `/api/tickets/${ticketId}/checklists`),
  availableTemplates: (ticketId: string) =>
    request<{ items: AvailableChecklistTemplate[] }>(
      "GET",
      `/api/tickets/${ticketId}/checklists/available-templates`,
    ),
  attach: (ticketId: string, templateId: string) =>
    request<TicketChecklist>("POST", `/api/tickets/${ticketId}/checklists`, { templateId }),
  detach: (ticketId: string, checklistId: string) =>
    request<void>("DELETE", `/api/tickets/${ticketId}/checklists/${checklistId}`),
  setItemState: (
    ticketId: string,
    itemId: string,
    state: ChecklistItemState,
    reason?: string,
    comment?: string,
  ) =>
    request<TicketChecklistItem>(
      "PATCH",
      `/api/tickets/${ticketId}/checklists/items/${itemId}/state`,
      { state, reason: reason ?? null, comment: comment ?? null },
    ),
  addComment: (ticketId: string, itemId: string, comment: string) =>
    request<TicketChecklistItem>(
      "POST",
      `/api/tickets/${ticketId}/checklists/items/${itemId}/comments`,
      { comment },
    ),
  addItem: (ticketId: string, checklistId: string, input: ChecklistItemInput) =>
    request<TicketChecklistItem>(
      "POST",
      `/api/tickets/${ticketId}/checklists/${checklistId}/items`,
      input,
    ),
  updateItem: (ticketId: string, itemId: string, input: ChecklistItemInput) =>
    request<TicketChecklistItem>("PUT", `/api/tickets/${ticketId}/checklists/items/${itemId}`, input),
  removeItem: (ticketId: string, itemId: string) =>
    request<void>("DELETE", `/api/tickets/${ticketId}/checklists/items/${itemId}`),
  itemEvents: (ticketId: string, itemId: string) =>
    request<{ items: ChecklistItemEvent[] }>(
      "GET",
      `/api/tickets/${ticketId}/checklists/items/${itemId}/events`,
    ),
};

// ---- admin templates ----

export type ChecklistTemplateSummary = {
  id: string;
  name: string;
  description: string;
  isActive: boolean;
  blockClose: boolean;
  queueIds: string[];
  itemCount: number;
  createdUtc: string;
  updatedUtc: string;
};

export type ChecklistTemplateItemDraft = {
  title: string;
  description: string;
  teamLabel: string;
  timingLabel: string;
  linkUrl: string;
  linkLabel: string;
  isRequired: boolean;
};

export type ChecklistTemplateSectionDraft = {
  title: string;
  items: ChecklistTemplateItemDraft[];
};

export type ChecklistTemplateDetail = ChecklistTemplateSummary & {
  sections: ChecklistTemplateSectionDraft[];
};

export type ChecklistTemplateInput = {
  name: string;
  description: string;
  isActive: boolean;
  blockClose: boolean;
  queueIds: string[];
  sections: ChecklistTemplateSectionDraft[];
};

export const checklistTemplateApi = {
  list: () => request<{ items: ChecklistTemplateSummary[] }>("GET", "/api/settings/checklist-templates"),
  get: (id: string) => request<ChecklistTemplateDetail>("GET", `/api/settings/checklist-templates/${id}`),
  create: (input: ChecklistTemplateInput) =>
    request<ChecklistTemplateDetail>("POST", "/api/settings/checklist-templates", input),
  update: (id: string, input: ChecklistTemplateInput) =>
    request<ChecklistTemplateDetail>("PUT", `/api/settings/checklist-templates/${id}`, input),
  duplicate: (id: string) =>
    request<ChecklistTemplateDetail>("POST", `/api/settings/checklist-templates/${id}/duplicate`),
  remove: (id: string) => request<void>("DELETE", `/api/settings/checklist-templates/${id}`),
};

// ---- helpers shared by bar / header / panel / list chip ----

export function checklistIsComplete(c: Pick<TicketChecklist, "completedUtc">): boolean {
  return c.completedUtc !== null;
}

/** First open required item (falls back to any open item) — "where I left off". */
export function nextOpenItem(c: TicketChecklist): TicketChecklistItem | null {
  const sorted = [...c.items].sort((a, b) => a.sortOrder - b.sortOrder);
  return (
    sorted.find((i) => i.state === "open" && i.isRequired) ??
    sorted.find((i) => i.state === "open") ??
    null
  );
}
