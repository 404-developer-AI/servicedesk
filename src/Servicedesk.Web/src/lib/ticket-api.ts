import { csrfHeader } from "@/lib/csrf";

export class ApiError extends Error {
  readonly status: number;
  readonly url: string;
  constructor(status: number, url: string, message: string) {
    super(message);
    this.status = status;
    this.url = url;
  }
}

async function request<T>(
  method: string,
  url: string,
  body?: unknown,
): Promise<T> {
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
    throw new ApiError(
      res.status,
      url,
      `${url} → ${res.status} ${res.statusText}`,
    );
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

// ---- Ticket types ----

export type TicketListItem = {
  id: string;
  number: number;
  subject: string;
  queueId: string;
  queueName: string;
  statusId: string;
  statusName: string;
  statusStateCategory: string;
  priorityId: string;
  priorityName: string;
  priorityLevel: number;
  requesterContactId: string;
  requesterEmail: string;
  requesterFirstName: string;
  requesterLastName: string;
  requesterCompanyId: string | null;
  companyName: string | null;
  assigneeUserId: string | null;
  assigneeEmail: string | null;
  createdUtc: string;
  updatedUtc: string;
  dueUtc: string | null;
};

export type TicketPage = {
  items: TicketListItem[];
  nextCursor: { updatedUtc: string; id: string } | null;
};

export type Ticket = {
  id: string;
  number: number;
  subject: string;
  requesterContactId: string;
  assigneeUserId: string | null;
  queueId: string;
  statusId: string;
  priorityId: string;
  categoryId: string | null;
  source: string;
  externalRef: string | null;
  createdUtc: string;
  updatedUtc: string;
  dueUtc: string | null;
  firstResponseUtc: string | null;
  resolvedUtc: string | null;
  closedUtc: string | null;
  isDeleted: boolean;
};

export type TicketBody = {
  ticketId: string;
  bodyText: string;
  bodyHtml: string | null;
};

export type TicketEvent = {
  id: number;
  ticketId: string;
  eventType: string;
  authorUserId: string | null;
  authorContactId: string | null;
  bodyText: string | null;
  bodyHtml: string | null;
  metadataJson: string;
  isInternal: boolean;
  createdUtc: string;
};

export type TicketDetail = {
  ticket: Ticket;
  body: TicketBody;
  events: TicketEvent[];
};

export type TicketListQuery = {
  queueId?: string;
  statusId?: string;
  priorityId?: string;
  assigneeUserId?: string;
  requesterContactId?: string;
  search?: string;
  openOnly?: boolean;
  cursorUpdatedUtc?: string;
  cursorId?: string;
  limit?: number;
};

export type CreateTicketRequest = {
  subject: string;
  bodyText?: string;
  bodyHtml?: string;
  requesterContactId: string;
  queueId: string;
  statusId: string;
  priorityId: string;
  categoryId?: string;
  assigneeUserId?: string;
  source?: string;
};

export type TicketFieldUpdate = {
  queueId?: string;
  statusId?: string;
  priorityId?: string;
  categoryId?: string;
  assigneeUserId?: string;
};

export type NewTicketEvent = {
  eventType: "Comment" | "Note";
  bodyText?: string;
  bodyHtml?: string;
  isInternal?: boolean;
};

// ---- Views ----

export type View = {
  id: string;
  userId: string;
  name: string;
  filtersJson: string;
  sortOrder: number;
  isShared: boolean;
  createdUtc: string;
  updatedUtc: string;
};

export type ViewInput = {
  name: string;
  filtersJson?: string;
  sortOrder?: number;
  isShared?: boolean;
};

// ---- Users ----

export type AgentUser = {
  id: string;
  email: string;
  roleName: string;
};

// ---- Contacts ----

export type Contact = {
  id: string;
  companyId: string | null;
  companyRole: string;
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  jobTitle: string;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
};

export type ContactInput = {
  email: string;
  companyId?: string | null;
  companyRole?: string;
  firstName?: string;
  lastName?: string;
  phone?: string;
  jobTitle?: string;
  isActive?: boolean;
};

export type Company = {
  id: string;
  name: string;
  description: string;
  website: string;
  phone: string;
  isActive: boolean;
};

// ---- API functions ----

export const ticketApi = {
  list: (query: TicketListQuery = {}) => {
    const params = new URLSearchParams();
    if (query.queueId) params.set("queueId", query.queueId);
    if (query.statusId) params.set("statusId", query.statusId);
    if (query.priorityId) params.set("priorityId", query.priorityId);
    if (query.assigneeUserId)
      params.set("assigneeUserId", query.assigneeUserId);
    if (query.requesterContactId)
      params.set("requesterContactId", query.requesterContactId);
    if (query.search) params.set("search", query.search);
    if (query.openOnly) params.set("openOnly", "true");
    if (query.cursorUpdatedUtc)
      params.set("cursorUpdatedUtc", query.cursorUpdatedUtc);
    if (query.cursorId) params.set("cursorId", query.cursorId);
    if (query.limit) params.set("limit", String(query.limit));
    const qs = params.toString();
    return request<TicketPage>("GET", `/api/tickets${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<TicketDetail>("GET", `/api/tickets/${id}`),
  create: (input: CreateTicketRequest) =>
    request<Ticket>("POST", "/api/tickets", input),
  update: (id: string, fields: TicketFieldUpdate) =>
    request<TicketDetail>("PATCH", `/api/tickets/${id}`, fields),
  addEvent: (id: string, event: NewTicketEvent) =>
    request<TicketEvent>("POST", `/api/tickets/${id}/events`, event),
};

export const viewApi = {
  list: () => request<View[]>("GET", "/api/views"),
  get: (id: string) => request<View>("GET", `/api/views/${id}`),
  create: (input: ViewInput) => request<View>("POST", "/api/views", input),
  update: (id: string, input: ViewInput) =>
    request<View>("PUT", `/api/views/${id}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/views/${id}`),
};

export const userApi = {
  listAgents: () => request<AgentUser[]>("GET", "/api/users"),
};

export const contactApi = {
  list: (search?: string, companyId?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    if (companyId) params.set("companyId", companyId);
    const qs = params.toString();
    return request<Contact[]>("GET", `/api/contacts${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<Contact>("GET", `/api/contacts/${id}`),
  create: (input: ContactInput) =>
    request<Contact>("POST", "/api/contacts", input),
  update: (id: string, input: ContactInput) =>
    request<Contact>("PUT", `/api/contacts/${id}`, input),
};

export const companyApi = {
  list: (search?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    const qs = params.toString();
    return request<Company[]>("GET", `/api/companies${qs ? `?${qs}` : ""}`);
  },
};
