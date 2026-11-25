import { request } from "@/lib/http";

/// v0.1.67 — maintenance notes per plan member: the original written in the
/// planner plus additions written during an intervention. ONE live set, shown
/// and edited on every round ticket of the member and in the planner (never a
/// copy on the ticket). Internal only.

export interface MaintenanceNoteSubject {
  memberId: string;
  planId: string;
  planName: string;
  companyName: string | null;
  contactName: string | null;
  label: string;
}

export interface MaintenanceNote {
  id: string;
  bodyHtml: string;
  ticketId: string | null;
  /** Null when written in the planner, or when the ticket is outside your queues. */
  ticketNumber: number | null;
  createdByEmail: string | null;
  createdUtc: string;
  updatedByEmail: string | null;
  updatedUtc: string | null;
}

export interface MaintenanceNoteRevision {
  noteId: string;
  action: "Added" | "Edited" | "Deleted";
  bodyHtml: string;
  ticketNumber: number | null;
  byEmail: string | null;
  createdUtc: string;
}

export interface MaintenanceNotesView {
  subject: MaintenanceNoteSubject | null;
  notes: MaintenanceNote[];
}

/// One door onto the notes: a round ticket or the plan member.
export interface MaintenanceNotesDoor {
  key: readonly unknown[];
  list: () => Promise<MaintenanceNotesView>;
  history: () => Promise<{ items: MaintenanceNoteRevision[] }>;
  add: (bodyHtml: string) => Promise<unknown>;
  update: (noteId: string, bodyHtml: string) => Promise<unknown>;
  remove: (noteId: string) => Promise<unknown>;
}

export const MAINTENANCE_NOTES_KEY = ["maintenance-notes"] as const;
const enc = encodeURIComponent;

function door(base: string, key: readonly unknown[]): MaintenanceNotesDoor {
  return {
    key,
    list: () => request<MaintenanceNotesView>("GET", base),
    history: () => request<{ items: MaintenanceNoteRevision[] }>("GET", `${base}/history`),
    add: (bodyHtml) => request<{ id: string }>("POST", base, { bodyHtml }),
    update: (noteId, bodyHtml) => request<void>("PUT", `${base}/${enc(noteId)}`, { bodyHtml }),
    remove: (noteId) => request<void>("DELETE", `${base}/${enc(noteId)}`),
  };
}

/// From a ticket: any agent with access to that ticket.
export const ticketNotesDoor = (ticketId: string) =>
  door(`/api/tickets/${enc(ticketId)}/maintenance-notes`, [...MAINTENANCE_NOTES_KEY, "ticket", ticketId]);

/// From the planner: Campaigns flag holders.
export const memberNotesDoor = (memberId: string) =>
  door(`/api/maintenance/members/${enc(memberId)}/notes`, [...MAINTENANCE_NOTES_KEY, "member", memberId]);
