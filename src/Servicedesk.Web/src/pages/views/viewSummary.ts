import { type DisplayConfig } from "@/lib/ticket-api";
import { type Queue, type Priority, type Status } from "@/lib/taxonomy-api";

// ---- Sort field options ----

export const SORT_FIELDS: { value: string; label: string }[] = [
  { value: "updatedUtc", label: "Updated" },
  { value: "createdUtc", label: "Created" },
  { value: "dueUtc", label: "Due date" },
  { value: "pendingTillUtc", label: "Pending till" },
  { value: "priorityLevel", label: "Priority" },
  { value: "number", label: "Ticket #" },
  { value: "subject", label: "Subject" },
  { value: "statusName", label: "Status" },
  { value: "queueName", label: "Queue" },
  { value: "assigneeEmail", label: "Assignee" },
  { value: "requesterEmail", label: "Requester" },
  { value: "companyName", label: "Company" },
  { value: "categoryName", label: "Category" },
  { value: "timeLogged", label: "Time logged" },
];

// ---- Group-by options ----

export const GROUP_BY_OPTIONS: { value: string; label: string; hasTaxonomy: boolean }[] = [
  { value: "", label: "None", hasTaxonomy: false },
  { value: "statusId", label: "Status", hasTaxonomy: true },
  { value: "priorityId", label: "Priority", hasTaxonomy: true },
  { value: "queueId", label: "Queue", hasTaxonomy: true },
  { value: "assigneeUserId", label: "Assignee", hasTaxonomy: false },
  { value: "categoryId", label: "Category", hasTaxonomy: true },
  { value: "companyName", label: "Company", hasTaxonomy: false },
  { value: "requesterContactId", label: "Requester", hasTaxonomy: false },
];

// ---- Filter shape stored in filtersJson ----
//
// v0.0.40 polish — queue / status / priority each accept multiple ids.
// Legacy views written before this change still have the singular
// `queueId` / `statusId` / `priorityId` fields; `normaliseFilters` folds
// those into the array form at read time so no DB migration is needed.

export type ViewFilters = {
  queueIds?: string[];
  statusIds?: string[];
  priorityIds?: string[];
  openOnly?: boolean;
  /// v0.0.105 — only project tickets, regardless of queue. Lets one view
  /// collect every project across the whole install.
  projectsOnly?: boolean;
  /// v0.1.17 — only tickets flagged Call-back / Research (both = either).
  callbacksOnly?: boolean;
  researchOnly?: boolean;
  search?: string;
};

export type LegacyViewFilters = ViewFilters & {
  queueId?: string;
  statusId?: string;
  priorityId?: string;
};

export function normaliseFilters(raw: LegacyViewFilters): ViewFilters {
  const queueIds = raw.queueIds ?? (raw.queueId ? [raw.queueId] : undefined);
  const statusIds = raw.statusIds ?? (raw.statusId ? [raw.statusId] : undefined);
  const priorityIds =
    raw.priorityIds ?? (raw.priorityId ? [raw.priorityId] : undefined);
  return {
    queueIds: queueIds && queueIds.length > 0 ? queueIds : undefined,
    statusIds: statusIds && statusIds.length > 0 ? statusIds : undefined,
    priorityIds:
      priorityIds && priorityIds.length > 0 ? priorityIds : undefined,
    openOnly: raw.openOnly,
    projectsOnly: raw.projectsOnly,
    callbacksOnly: raw.callbacksOnly,
    researchOnly: raw.researchOnly,
    search: raw.search,
  };
}

function joinNames<T extends { id: string; name: string }>(
  ids: string[],
  source: T[],
): string {
  return ids
    .map((id) => source.find((s) => s.id === id)?.name)
    .filter((n): n is string => !!n)
    .join(", ");
}

export function formatFilters(
  filtersJson: string,
  queues: Queue[],
  statuses: Status[],
  priorities: Priority[],
): string[] {
  try {
    const f = normaliseFilters(JSON.parse(filtersJson) as LegacyViewFilters);
    const parts: string[] = [];
    if (f.queueIds && f.queueIds.length > 0) {
      const names = joinNames(f.queueIds, queues);
      if (names) parts.push(`Queue: ${names}`);
    }
    if (f.statusIds && f.statusIds.length > 0) {
      const names = joinNames(f.statusIds, statuses);
      if (names) parts.push(`Status: ${names}`);
    }
    if (f.priorityIds && f.priorityIds.length > 0) {
      const names = joinNames(f.priorityIds, priorities);
      if (names) parts.push(`Priority: ${names}`);
    }
    if (f.openOnly) parts.push("Open only");
    if (f.projectsOnly) parts.push("Projects only");
    if (f.callbacksOnly) parts.push("Call-backs only");
    if (f.researchOnly) parts.push("Research only");
    if (f.search) parts.push(`Search: "${f.search}"`);
    return parts;
  } catch {
    return [];
  }
}

export function formatDisplayConfig(dc: DisplayConfig): string[] {
  const parts: string[] = [];
  if (dc.priorityFloat) parts.push("Priority float");
  if (dc.callbackFloat) parts.push("Call-back float");
  if (dc.researchFloat) parts.push("Research float");
  if (dc.groupBy) {
    const opt = GROUP_BY_OPTIONS.find((o) => o.value === dc.groupBy);
    if (opt) parts.push(`Group: ${opt.label}`);
  }
  if (dc.sort?.field) {
    const sf = SORT_FIELDS.find((f) => f.value === dc.sort!.field);
    const dir = dc.sort.direction === "asc" ? "\u2191" : "\u2193";
    if (sf) parts.push(`Sort: ${sf.label} ${dir}`);
  }
  if (dc.searchEnabled) parts.push(`Search (${dc.searchDefaultMode === "full" ? "full" : "columns"})`);
  if (dc.tabCategoryIds?.length) parts.push(`Tabs: ${dc.tabCategoryIds.length + 1}`);
  return parts;
}
