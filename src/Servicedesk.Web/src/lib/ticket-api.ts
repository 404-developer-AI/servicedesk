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

/// Which branch of the mail-intake decision tree (or post-creation action)
/// settled the ticket's company. 'unresolved' pairs with awaitingCompanyAssignment=true.
export type CompanyResolvedVia =
  | "thread_reply"
  | "primary"
  | "secondary"
  | "manual"
  | "unresolved";

export type TicketListItem = {
  id: string;
  number: number;
  subject: string;
  queueId: string;
  queueName: string;
  statusId: string;
  statusName: string;
  statusColor: string;
  statusStateCategory: string;
  priorityId: string;
  priorityName: string;
  priorityLevel: number;
  priorityColor: string;
  priorityIsDefault: boolean;
  requesterContactId: string;
  requesterEmail: string;
  requesterFirstName: string;
  requesterLastName: string;
  requesterCompanyId: string | null;
  companyName: string | null;
  assigneeUserId: string | null;
  assigneeEmail: string | null;
  categoryId: string | null;
  categoryName: string | null;
  createdUtc: string;
  updatedUtc: string;
  dueUtc: string | null;
  awaitingCompanyAssignment: boolean;
  companyResolvedVia: CompanyResolvedVia | null;
};

export type TicketPage = {
  items: TicketListItem[];
  nextCursor: { updatedUtc: string; id: string } | null;
  nextOffset: number | null;
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
  companyId: string | null;
  awaitingCompanyAssignment: boolean;
  companyResolvedVia: CompanyResolvedVia | null;
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
  authorName: string | null;
  bodyText: string | null;
  bodyHtml: string | null;
  metadataJson: string;
  isInternal: boolean;
  createdUtc: string;
  editedUtc: string | null;
  editedByUserId: string | null;
};

export type TicketEventRevision = {
  id: number;
  eventId: number;
  revisionNumber: number;
  bodyTextBefore: string | null;
  bodyHtmlBefore: string | null;
  isInternalBefore: boolean;
  editedByUserId: string;
  editedByName: string | null;
  editedUtc: string;
};

export type UpdateTicketEventRequest = {
  bodyText?: string;
  bodyHtml?: string;
  isInternal?: boolean;
};

export type TicketEventPin = {
  id: number;
  eventId: number;
  ticketId: string;
  pinnedByUserId: string;
  pinnedByName: string | null;
  remark: string;
  createdUtc: string;
};

export type CompanyAlert = {
  companyId: string;
  companyName: string;
  code: string;
  alertText: string;
  alertOnCreate: boolean;
  alertOnOpen: boolean;
  alertOnOpenMode: "session" | "every";
};

export type TicketDetail = {
  ticket: Ticket;
  body: TicketBody;
  events: TicketEvent[];
  pinnedEvents: TicketEventPin[];
  companyAlert: CompanyAlert | null;
};

export type CreateTicketResponse = {
  ticket: Ticket;
  companyAlert: CompanyAlert | null;
  showAlertOnCreate: boolean;
};

export type TicketListQuery = {
  queueId?: string;
  statusId?: string;
  priorityId?: string;
  assigneeUserId?: string;
  requesterContactId?: string;
  search?: string;
  openOnly?: boolean;
  sortField?: string;
  sortDirection?: string;
  priorityFloat?: boolean;
  offset?: number;
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
  subject?: string;
  bodyText?: string;
  bodyHtml?: string;
};

export type NewTicketEvent = {
  eventType: "Comment" | "Note";
  bodyText?: string;
  bodyHtml?: string;
  isInternal?: boolean;
  /// Ids of attachments uploaded via /api/tickets/{id}/attachments while the
  /// post was being composed. The server flips them onto the freshly-created
  /// event in the same request; mismatched ids (other ticket, already linked)
  /// are silently dropped so a stale draft can't fail the submit.
  attachmentIds?: string[];
  /// Agent user-ids tagged via @@-mention in the editor body. Filtered
  /// server-side against the Agent+Admin set; unknown / customer / deleted
  /// ids are silently dropped before the event metadata is written.
  mentionedUserIds?: string[];
};

export type TicketAttachmentMeta = {
  id: string;
  url: string;
  mimeType: string;
  size: number;
  filename: string;
};

export type OutboundMailKind = "Reply" | "ReplyAll" | "New" | "Forward";

export type MailRecipientInput = {
  address: string;
  name?: string;
};

export type SendOutboundMailRequest = {
  kind: OutboundMailKind;
  to?: MailRecipientInput[];
  cc?: MailRecipientInput[];
  bcc?: MailRecipientInput[];
  subject: string;
  bodyHtml: string;
  /// Attachments uploaded via /api/tickets/{id}/attachments while composing.
  /// Server resolves them, detects inline (URL appears in bodyHtml AND mime
  /// is image/*), rewrites those URLs to cid:{generated}, and ships the
  /// rest as plain Graph file-attachments. Total bytes capped at
  /// Mail.MaxOutboundTotalBytes — exceeding it returns 413.
  attachmentIds?: string[];
  /// Agent user-ids tagged via @@-mention. See NewTicketEvent for semantics.
  mentionedUserIds?: string[];
};

// ---- Views ----

export type DisplayConfig = {
  priorityFloat?: boolean;
  groupBy?: string | null;
  groupOrder?: string[] | null;
  sort?: { field: string; direction: "asc" | "desc" } | null;
};

export type View = {
  id: string;
  userId: string;
  name: string;
  filtersJson: string;
  columns: string | null;
  sortOrder: number;
  isShared: boolean;
  displayConfigJson: string;
  createdUtc: string;
  updatedUtc: string;
};

export type ViewInput = {
  name: string;
  filtersJson?: string;
  columns?: string | null;
  sortOrder?: number;
  isShared?: boolean;
  displayConfigJson?: string;
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
  /// The id of the company the contact is linked to with role='primary'.
  /// Read-only — server computes this from the contact_companies join table.
  /// A contact may also have secondary/supplier links, but those live on
  /// the contact-detail view, not here.
  primaryCompanyId: string | null;
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

/// Row shape returned by /api/contacts/browse — the paginated overview
/// that backs the `/contacts` page. Flattens the primary-link metadata onto
/// the contact row and adds `extraLinkCount` + `lastTicketUpdatedUtc` so
/// the table can render without N+1 round-trips.
export type ContactListItem = {
  id: string;
  companyRole: string;
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  jobTitle: string;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
  primaryCompanyId: string | null;
  primaryCompanyName: string | null;
  primaryCompanyCode: string | null;
  primaryCompanyShortName: string | null;
  primaryCompanyIsActive: boolean;
  extraLinkCount: number;
  lastTicketUpdatedUtc: string | null;
};

export type ContactOverviewPage = {
  items: ContactListItem[];
  total: number;
  page: number;
  pageSize: number;
};

export type ContactAuditEntry = {
  id: number;
  utc: string;
  actor: string;
  actorRole: string;
  eventType: string;
  target: string | null;
  payloadJson: string;
};

export type ContactAuditPage = {
  items: ContactAuditEntry[];
  nextCursor: number | null;
};

export type ContactBrowseQuery = {
  search?: string;
  companyId?: string;
  role?: ContactCompanyRole | "none";
  includeInactive?: boolean;
  sort?: "name_asc" | "email_asc" | "last_activity_desc";
  page?: number;
  pageSize?: number;
};

export type ContactInput = {
  email: string;
  /// Shorthand on create/update: when set the server also inserts a
  /// link to this company in the same transaction. On POST /api/contacts
  /// the role below decides whether it's primary/secondary/supplier; on
  /// PUT the link is always upserted as 'primary' (legacy behaviour).
  companyId?: string | null;
  /// Only honoured on POST when companyId is set. Defaults to 'primary'.
  role?: ContactCompanyRole;
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
  email: string;
  addressLine1: string;
  addressLine2: string;
  city: string;
  postalCode: string;
  country: string;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
  code: string;
  shortName: string;
  vatNumber: string;
  alertText: string;
  alertOnCreate: boolean;
  alertOnOpen: boolean;
  alertOnOpenMode: "session" | "every";
};

export type CompanyInput = {
  name: string;
  code: string;
  shortName?: string;
  vatNumber?: string;
  email?: string;
  description?: string;
  website?: string;
  phone?: string;
  addressLine1?: string;
  addressLine2?: string;
  city?: string;
  postalCode?: string;
  country?: string;
  isActive?: boolean;
  alertText?: string;
  alertOnCreate?: boolean;
  alertOnOpen?: boolean;
  alertOnOpenMode?: "session" | "every";
};

export type CompanyDomain = {
  id: string;
  companyId: string;
  domain: string;
  createdUtc: string;
};

export type CompanyDetail = {
  company: Company;
  domains: CompanyDomain[];
};

export type ContactCompanyRole = "primary" | "secondary" | "supplier";

/// One role-tagged link between a contact and a company. Returned by
/// /api/companies/{id}/links for the Contacts-tab so each contact row can
/// render with its role badge relative to *this* company.
export type ContactCompanyLink = {
  id: string;
  contactId: string;
  companyId: string;
  role: ContactCompanyRole;
  createdUtc: string;
  updatedUtc: string;
};

/// One entry in the contact's role-annotated company list — used by the
/// ticket company-assignment dialog and future contact-detail view.
export type ContactCompanyOption = {
  linkId: string;
  companyId: string;
  companyName: string;
  companyCode: string;
  companyShortName: string;
  companyIsActive: boolean;
  role: ContactCompanyRole;
};

/// Minimal company row returned by the agent-readable picker endpoint.
export type CompanyPickerItem = {
  id: string;
  name: string;
  code: string;
  shortName: string;
  isActive: boolean;
};

export type AssignTicketCompanyRequest = {
  companyId: string;
  linkAsSupplier: boolean;
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
    if (query.sortField) params.set("sortField", query.sortField);
    if (query.sortDirection) params.set("sortDirection", query.sortDirection);
    if (query.priorityFloat) params.set("priorityFloat", "true");
    if (query.offset != null) params.set("offset", String(query.offset));
    if (query.cursorUpdatedUtc)
      params.set("cursorUpdatedUtc", query.cursorUpdatedUtc);
    if (query.cursorId) params.set("cursorId", query.cursorId);
    if (query.limit) params.set("limit", String(query.limit));
    const qs = params.toString();
    return request<TicketPage>("GET", `/api/tickets${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<TicketDetail>("GET", `/api/tickets/${id}`),
  create: (input: CreateTicketRequest) =>
    request<CreateTicketResponse>("POST", "/api/tickets", input),
  update: (id: string, fields: TicketFieldUpdate) =>
    request<TicketDetail>("PATCH", `/api/tickets/${id}`, fields),
  addEvent: (id: string, event: NewTicketEvent) =>
    request<TicketEvent>("POST", `/api/tickets/${id}/events`, event),
  sendMail: (id: string, payload: SendOutboundMailRequest) =>
    request<TicketEvent>("POST", `/api/tickets/${id}/mail`, payload),
  updateEvent: (id: string, eventId: number, body: UpdateTicketEventRequest) =>
    request<TicketEvent>("PUT", `/api/tickets/${id}/events/${eventId}`, body),
  getEventRevisions: (id: string, eventId: number) =>
    request<TicketEventRevision[]>("GET", `/api/tickets/${id}/events/${eventId}/revisions`),
  pinEvent: (id: string, eventId: number, remark?: string) =>
    request<TicketEventPin>("POST", `/api/tickets/${id}/events/${eventId}/pin`, { remark }),
  unpinEvent: (id: string, eventId: number) =>
    request<void>("DELETE", `/api/tickets/${id}/events/${eventId}/pin`),
  updatePinRemark: (id: string, eventId: number, remark: string) =>
    request<TicketEventPin>("PATCH", `/api/tickets/${id}/events/${eventId}/pin`, { remark }),
  assignCompany: (id: string, body: AssignTicketCompanyRequest) =>
    request<TicketDetail>("PATCH", `/api/tickets/${id}/company`, body),
  changeRequester: (id: string, contactId: string) =>
    request<TicketDetail>("PATCH", `/api/tickets/${id}/requester`, { contactId }),
  exportPdf: (id: string, excludeInternal = true) => {
    const params = new URLSearchParams();
    if (!excludeInternal) params.set("excludeInternal", "false");
    const qs = params.toString();
    return `/api/tickets/${id}/export/pdf${qs ? `?${qs}` : ""}`;
  },
  /// Multipart upload of a single file. The server streams to IBlobStore,
  /// sniffs MIME, enforces the size cap, and returns metadata + the
  /// session-cookie-authenticated URL the editor can use as <img src>.
  uploadAttachment: async (
    ticketId: string,
    file: File,
  ): Promise<TicketAttachmentMeta> => {
    const form = new FormData();
    form.append("file", file, file.name);
    const url = `/api/tickets/${ticketId}/attachments`;
    const res = await fetch(url, {
      method: "POST",
      credentials: "include",
      headers: csrfHeader(),
      body: form,
    });
    if (!res.ok) {
      let message = `Upload failed (${res.status})`;
      try {
        const body = (await res.json()) as { error?: string };
        if (body?.error) message = body.error;
      } catch {
        /* ignore */
      }
      throw new ApiError(res.status, url, message);
    }
    return (await res.json()) as TicketAttachmentMeta;
  },
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
  /// Typeahead for the @@-mention popover. Agent + Admin only; customers
  /// never appear. Empty `q` returns the top-N alphabetically.
  searchAgents: (q: string, limit = 20) => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    params.set("limit", String(limit));
    return request<AgentUser[]>("GET", `/api/users/agents/search?${params.toString()}`);
  },
};

export const contactApi = {
  list: (search?: string, companyId?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    if (companyId) params.set("companyId", companyId);
    const qs = params.toString();
    return request<Contact[]>("GET", `/api/contacts${qs ? `?${qs}` : ""}`);
  },
  browse: (query: ContactBrowseQuery = {}) => {
    const params = new URLSearchParams();
    if (query.search) params.set("search", query.search);
    if (query.companyId) params.set("companyId", query.companyId);
    if (query.role) params.set("role", query.role);
    if (query.includeInactive) params.set("includeInactive", "true");
    if (query.sort) params.set("sort", query.sort);
    if (query.page) params.set("page", String(query.page));
    if (query.pageSize) params.set("pageSize", String(query.pageSize));
    const qs = params.toString();
    return request<ContactOverviewPage>("GET", `/api/contacts/browse${qs ? `?${qs}` : ""}`);
  },
  audit: (id: string, cursor?: number, limit?: number) => {
    const params = new URLSearchParams();
    if (cursor) params.set("cursor", String(cursor));
    if (limit) params.set("limit", String(limit));
    const qs = params.toString();
    return request<ContactAuditPage>("GET", `/api/contacts/${id}/audit${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<Contact>("GET", `/api/contacts/${id}`),
  listCompanies: (id: string) =>
    request<ContactCompanyOption[]>("GET", `/api/contacts/${id}/companies`),
  create: (input: ContactInput) =>
    request<Contact>("POST", "/api/contacts", input),
  update: (id: string, input: ContactInput) =>
    request<Contact>("PUT", `/api/contacts/${id}`, input),
};

export const companyApi = {
  list: (search?: string, includeInactive = false) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    if (includeInactive) params.set("includeInactive", "true");
    const qs = params.toString();
    return request<Company[]>("GET", `/api/companies${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<CompanyDetail>("GET", `/api/companies/${id}`),
  picker: (search?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    const qs = params.toString();
    return request<CompanyPickerItem[]>("GET", `/api/companies/picker${qs ? `?${qs}` : ""}`);
  },
  create: (input: CompanyInput) => request<Company>("POST", "/api/companies", input),
  update: (id: string, input: CompanyInput) =>
    request<Company>("PUT", `/api/companies/${id}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/companies/${id}`),
  listContacts: (id: string, search?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    const qs = params.toString();
    return request<Contact[]>("GET", `/api/companies/${id}/contacts${qs ? `?${qs}` : ""}`);
  },
  links: (id: string) =>
    request<ContactCompanyLink[]>("GET", `/api/companies/${id}/links`),
  linkContact: (companyId: string, contactId: string, role?: ContactCompanyRole) =>
    request<void>(
      "POST",
      `/api/companies/${companyId}/contacts/${contactId}`,
      role ? { role } : undefined,
    ),
  unlinkContact: (companyId: string, contactId: string) =>
    request<void>("DELETE", `/api/companies/${companyId}/contacts/${contactId}`),
  addDomain: (id: string, domain: string) =>
    request<CompanyDomain>("POST", `/api/companies/${id}/domains`, { domain }),
  removeDomain: (id: string, domainId: string) =>
    request<void>("DELETE", `/api/companies/${id}/domains/${domainId}`),
};
