import { request } from "@/lib/http";

// ---- Employee Feedback ----

export type FeedbackEmployee = { id: string; email: string };
export type FeedbackWorkPointType = {
  id: string;
  name: string;
  color: string;
  sortOrder: number;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
};
export type FeedbackEntryRow = {
  id: string;
  targetUserId: string | null;
  targetUserEmail: string | null;
  entryDate: string;
  bodyHtml: string | null;
  managementRemarksHtml: string | null;
  workPointTypeId: string | null;
  workPointTypeName: string | null;
  workPointTypeColor: string | null;
  isCompleted: boolean;
  completedByUserId: string | null;
  completedByEmail: string | null;
  completedUtc: string | null;
  mgmtReviewed: boolean;
  mgmtReviewedByUserId: string | null;
  mgmtReviewedByEmail: string | null;
  mgmtReviewedUtc: string | null;
  linkedTicketId: string | null;
  linkedTicketNumber: number | null;
  linkedTicketSubject: string | null;
  linkedTicketEventId: number | null;
  source: "manual" | "activity";
  createdByUserId: string | null;
  createdByEmail: string | null;
  createdUtc: string;
  updatedUtc: string;
};
export type FeedbackLoggedEvent = {
  eventId: number;
  count: number;
  loggers: string[];
};
export type FeedbackLogInput = {
  targetUserId: string;
  entryDate: string;
  workPointTypeId: string | null;
  bodyHtml: string | null;
  linkedTicketId: string;
  linkedTicketNumber: number;
  linkedTicketEventId: number;
};
export type FeedbackEntryFilter = {
  targetUserId?: string;
  workPointTypeId?: string;
  completed?: boolean;
  mgmtReviewed?: boolean;
};
export type FeedbackEntryUpdate = {
  targetUserId: string | null;
  entryDate: string;
  bodyHtml: string | null;
  managementRemarksHtml: string | null;
  workPointTypeId: string | null;
  isCompleted: boolean;
  isMgmtReviewed: boolean;
  linkedTicketNumber: number | null;
};
export type FeedbackAttachment = {
  id: string;
  url: string;
  mimeType: string;
  size: number;
  filename: string;
};
export type FeedbackAdminWorkPointTypeInput = {
  name: string;
  color: string;
  sortOrder: number;
};
export type FeedbackAdminWorkPointTypeUpdate = {
  name: string;
  color: string;
  sortOrder: number;
  isActive: boolean;
};
export const feedbackApi = {
  listEmployees: () =>
    request<{ items: FeedbackEmployee[] }>("GET", "/api/feedback/employees"),

  listWorkPointTypes: () =>
    request<{ items: FeedbackWorkPointType[] }>("GET", "/api/feedback/work-point-types"),

  resolveTicket: (number: number) =>
    request<{ number: number; id: string; subject: string }>(
      "GET",
      `/api/feedback/resolve-ticket?number=${number}`,
    ),

  listEntries: (filter: FeedbackEntryFilter = {}) => {
    const qs = new URLSearchParams();
    if (filter.targetUserId) qs.set("targetUserId", filter.targetUserId);
    if (filter.workPointTypeId) qs.set("workPointTypeId", filter.workPointTypeId);
    if (filter.completed !== undefined) qs.set("completed", String(filter.completed));
    if (filter.mgmtReviewed !== undefined) qs.set("mgmtReviewed", String(filter.mgmtReviewed));
    const suffix = qs.toString();
    return request<{ items: FeedbackEntryRow[] }>(
      "GET",
      suffix ? `/api/feedback/entries?${suffix}` : "/api/feedback/entries",
    );
  },

  createEntry: (targetUserId?: string) =>
    request<FeedbackEntryRow>("POST", "/api/feedback/entries", { targetUserId }),

  logEntry: (body: FeedbackLogInput) =>
    request<FeedbackEntryRow>("POST", "/api/feedback/entries/log", body),

  listLoggedEvents: (ticketId: string) =>
    request<{ items: FeedbackLoggedEvent[] }>(
      "GET",
      `/api/feedback/logged-events?ticketId=${ticketId}`,
    ),

  updateEntry: (id: string, body: FeedbackEntryUpdate) =>
    request<FeedbackEntryRow>("PUT", `/api/feedback/entries/${id}`, body),

  setCompleted: (id: string, completed: boolean) =>
    request<FeedbackEntryRow>("POST", `/api/feedback/entries/${id}/completed`, { completed }),

  setMgmtReviewed: (id: string, reviewed: boolean) =>
    request<FeedbackEntryRow>("POST", `/api/feedback/entries/${id}/mgmt-reviewed`, { reviewed }),

  deleteEntry: (id: string) =>
    request<void>("DELETE", `/api/feedback/entries/${id}`),

  uploadAttachment: async (
    entryId: string,
    file: File,
  ): Promise<FeedbackAttachment> => {
    const fd = new FormData();
    fd.append("file", file, file.name);
    const url = `/api/feedback/entries/${entryId}/attachments`;
    return request<FeedbackAttachment>("POST", url, fd);
  },
};
export const feedbackAdminApi = {
  listWorkPointTypes: (includeInactive = false) => {
    const qs = includeInactive ? "?includeInactive=true" : "";
    return request<{ items: FeedbackWorkPointType[] }>(
      "GET",
      `/api/admin/feedback/work-point-types${qs}`,
    );
  },

  createWorkPointType: (body: FeedbackAdminWorkPointTypeInput) =>
    request<FeedbackWorkPointType>("POST", "/api/admin/feedback/work-point-types", body),

  updateWorkPointType: (id: string, body: FeedbackAdminWorkPointTypeUpdate) =>
    request<FeedbackWorkPointType>("PUT", `/api/admin/feedback/work-point-types/${id}`, body),

  deleteWorkPointType: (id: string) =>
    request<void>("DELETE", `/api/admin/feedback/work-point-types/${id}`),
};
