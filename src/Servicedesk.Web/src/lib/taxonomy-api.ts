import { request } from "@/lib/http";

// ---- Taxonomy ----

/// One inbound-mailbox source on a queue (v0.0.66). A queue can have several;
/// each is a (mailbox, folder) pair polled by Microsoft Graph into the queue.
export type QueueInboundMailbox = {
  id: string;
  mailbox: string;
  folderId?: string | null;
  folderName?: string | null;
  pollingEnabled: boolean;
  lastPolledUtc?: string | null;
  lastError?: string | null;
  consecutiveFailures: number;
};
export type Queue = {
  id: string;
  name: string;
  slug: string;
  description: string;
  color: string;
  icon: string;
  sortOrder: number;
  isActive: boolean;
  isSystem: boolean;
  createdUtc: string;
  updatedUtc: string;
  // Singular mirror of the first inbound source, kept for the outbound
  // from-address fallback + legacy readers. The editor uses inboundMailboxes.
  inboundMailboxAddress?: string | null;
  outboundMailboxAddress?: string | null;
  inboundFolderId?: string | null;
  inboundFolderName?: string | null;
  inboundPollingEnabled?: boolean;
  // Per-queue switch for the Claude AI ticket-assist action. Defaults true on
  // the server; gates the "Analyze & propose a solution by AI" button.
  aiAssistEnabled?: boolean;
  inboundMailboxes?: QueueInboundMailbox[];
  // v0.0.40 polish — per-queue status scope. Empty array = all
  // statuses available. Non-empty = dropdown filters to these ids.
  allowedStatusIds: string[];
  defaultStatusId: string | null;
  // v0.0.87 — per-queue override for the ticket hour-limit alert.
  // "inherit" uses the global Timesheet settings; "on"/"off" force it for
  // this queue. timeAlertThresholdMinutes overrides the global limit
  // (null = inherit the global limit).
  timeAlertMode?: "inherit" | "on" | "off";
  timeAlertThresholdMinutes?: number | null;
};
export type Priority = {
  id: string;
  name: string;
  slug: string;
  level: number;
  color: string;
  icon: string;
  sortOrder: number;
  isActive: boolean;
  isSystem: boolean;
  isDefault: boolean;
  createdUtc: string;
  updatedUtc: string;
};
export type StatusStateCategory =
  | "New"
  | "Open"
  | "Pending"
  | "Resolved"
  | "Closed";
export type Status = {
  id: string;
  name: string;
  slug: string;
  stateCategory: StatusStateCategory;
  color: string;
  icon: string;
  sortOrder: number;
  isActive: boolean;
  isSystem: boolean;
  isDefault: boolean;
  createdUtc: string;
  updatedUtc: string;
};
export type Category = {
  id: string;
  parentId: string | null;
  name: string;
  slug: string;
  description: string;
  sortOrder: number;
  isActive: boolean;
  isLocked: boolean;
  isSystem: boolean;
  createdUtc: string;
  updatedUtc: string;
};
/// One inbound-mailbox source as sent to the API on create/update. `id` present
/// = update an existing source; absent = add. Sources omitted from the list are
/// removed.
export type QueueInboundMailboxInput = {
  id?: string | null;
  mailbox: string;
  folderId?: string | null;
  folderName?: string | null;
  pollingEnabled: boolean;
};
export type QueueInput = {
  name: string;
  description?: string;
  color?: string;
  icon?: string;
  sortOrder: number;
  isActive: boolean;
  outboundMailboxAddress?: string | null;
  inboundMailboxes?: QueueInboundMailboxInput[];
  // v0.0.40 polish — per-queue status scope. Empty / null = current
  // behaviour (all statuses); non-empty = filter list. defaultStatusId
  // drives the auto-flip on queue change.
  allowedStatusIds?: string[];
  defaultStatusId?: string | null;
  aiAssistEnabled?: boolean;
  // v0.0.87 — per-queue hour-limit alert override.
  timeAlertMode?: "inherit" | "on" | "off";
  timeAlertThresholdMinutes?: number | null;
};
export type PriorityInput = {
  name: string;
  level: number;
  color?: string;
  icon?: string;
  sortOrder: number;
  isActive: boolean;
  isDefault: boolean;
};
export type StatusInput = {
  name: string;
  stateCategory: StatusStateCategory;
  color?: string;
  icon?: string;
  sortOrder: number;
  isActive: boolean;
  isDefault: boolean;
};
export type CategoryInput = {
  name: string;
  parentId?: string | null;
  description?: string;
  sortOrder: number;
  isActive: boolean;
  isLocked: boolean;
};
// v0.0.39 — ticket types. Seeded with support / order / iso27001; admins
// can add their own. Drives the "Create linked X ticket" picker in the
// ticket side panel — each active manual trigger is bound to one of
// these rows and produces a ticket of that type.
export type TicketType = {
  id: string;
  code: string;
  label: string;
  description: string;
  icon: string;
  color: string;
  sortOrder: number;
  isActive: boolean;
  isSystem: boolean;
  createdUtc: string;
  updatedUtc: string;
};
export type TicketTypeInput = {
  code: string;
  label: string;
  description?: string;
  icon?: string;
  color?: string;
  sortOrder: number;
  isActive: boolean;
};
function crud<T, TInput>(base: string) {
  return {
    list: () => request<T[]>("GET", base),
    get: (id: string) => request<T>("GET", `${base}/${id}`),
    create: (input: TInput) => request<T>("POST", base, input),
    update: (id: string, input: TInput) => request<T>("PUT", `${base}/${id}`, input),
    remove: (id: string) => request<void>("DELETE", `${base}/${id}`),
  };
}
export const taxonomyApi = {
  queues: crud<Queue, QueueInput>("/api/taxonomy/queues"),
  priorities: crud<Priority, PriorityInput>("/api/taxonomy/priorities"),
  statuses: crud<Status, StatusInput>("/api/taxonomy/statuses"),
  categories: crud<Category, CategoryInput>("/api/taxonomy/categories"),
  ticketTypes: crud<TicketType, TicketTypeInput>("/api/taxonomy/ticket-types"),
};
export const STATE_CATEGORIES: StatusStateCategory[] = [
  "New",
  "Open",
  "Pending",
  "Resolved",
  "Closed",
];
