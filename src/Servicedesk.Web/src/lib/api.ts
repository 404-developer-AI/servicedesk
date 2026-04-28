import { csrfHeader } from "@/lib/csrf";
import type { Role } from "@/lib/roles";

export type SystemVersion = {
  version: string;
  commit: string;
  buildTime: string;
};

export type SystemTime = {
  utc: string;
  timezone: string;
  offsetMinutes: number;
};

export type MaintenanceState = {
  active: boolean;
  startUtc: string | null;
  endUtc: string | null;
  message: string;
};

export type AuditEntry = {
  id: number;
  utc: string;
  actor: string;
  actorRole: string;
  eventType: string;
  target: string | null;
  clientIp: string | null;
  userAgent: string | null;
  payload: unknown;
  entryHash: string;
  prevHash: string;
};

export type AuditPage = {
  items: AuditEntry[];
  nextCursor: number | null;
};

export type AuditListQuery = {
  eventType?: string;
  actor?: string;
  fromUtc?: string;
  toUtc?: string;
  cursor?: number;
  limit?: number;
};

export type AuthUserPayload = {
  id: string;
  email: string;
  role: Role;
  amr: string;
  twoFactorEnabled: boolean;
};

export type MeResponse = {
  user: AuthUserPayload | null;
  serverTimeUtc: string;
};

export type SetupStatus = { available: boolean };

export type LoginResponse = {
  email: string;
  role: Role;
  twoFactorRequired: boolean;
};

export type TotpEnrollment = {
  secret: string;
  otpauthUri: string;
};

export type RecoveryCodesResponse = { recoveryCodes: string[] };

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
  init?: RequestInit,
): Promise<T> {
  const isSafe = method === "GET" || method === "HEAD";
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(isSafe ? {} : csrfHeader()),
      ...(init?.headers ?? {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    ...init,
  });
  if (!res.ok) {
    throw new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`);
  }
  if (res.status === 204) {
    return undefined as T;
  }
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export type HealthStatus = "Ok" | "Warning" | "Critical";

export type HealthDetail = { label: string; value: string | null };

export type HealthAction = {
  key: string;
  label: string;
  endpoint: string;
  confirmMessage: string | null;
};

export type SubsystemHealth = {
  key: string;
  label: string;
  status: HealthStatus;
  summary: string;
  details: HealthDetail[];
  actions: HealthAction[];
};

export type HealthReport = {
  status: HealthStatus;
  subsystems: SubsystemHealth[];
};

export const systemApi = {
  version: () => request<SystemVersion>("GET", "/api/system/version"),
  time: () => request<SystemTime>("GET", "/api/system/time"),
  health: () => request<{ status: HealthStatus }>("GET", "/api/system/health"),
  maintenance: () => request<MaintenanceState>("GET", "/api/system/maintenance"),
};

export type IncidentSeverity = "Warning" | "Critical";

export type IncidentRow = {
  id: number;
  subsystem: string;
  severity: IncidentSeverity;
  message: string;
  details: string | null;
  firstOccurredUtc: string;
  lastOccurredUtc: string;
  occurrenceCount: number;
  acknowledgedUtc: string | null;
  acknowledgedByUserId: string | null;
};

export const healthApi = {
  get: () => request<HealthReport>("GET", "/api/admin/health"),
  runAction: (endpoint: string) => request<void>("POST", endpoint),
  listIncidents: (take = 200) =>
    request<{ items: IncidentRow[] }>("GET", `/api/admin/health/incidents?take=${take}`),
  listArchive: (subsystem?: string, take = 100) => {
    const params = new URLSearchParams();
    params.set("take", String(take));
    if (subsystem) params.set("subsystem", subsystem);
    return request<{ items: IncidentRow[] }>(
      "GET",
      `/api/admin/health/incidents/archive?${params.toString()}`,
    );
  },
  acknowledge: (id: number) =>
    request<void>("POST", `/api/admin/health/incidents/${id}/ack`),
  acknowledgeSubsystem: (subsystem: string) =>
    request<{ acknowledged: number }>(
      "POST",
      `/api/admin/health/incidents/ack-subsystem/${encodeURIComponent(subsystem)}`,
    ),
};

export const auditApi = {
  list: (query: AuditListQuery = {}) => {
    const params = new URLSearchParams();
    if (query.eventType) params.set("eventType", query.eventType);
    if (query.actor) params.set("actor", query.actor);
    if (query.fromUtc) params.set("fromUtc", query.fromUtc);
    if (query.toUtc) params.set("toUtc", query.toUtc);
    if (query.cursor !== undefined) params.set("cursor", String(query.cursor));
    params.set("limit", String(query.limit ?? 50));
    return request<AuditPage>("GET", `/api/audit?${params.toString()}`);
  },
  get: (id: number) => request<AuditEntry>("GET", `/api/audit/${id}`),
};

// ---- Taxonomy ----

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
  inboundMailboxAddress?: string | null;
  outboundMailboxAddress?: string | null;
  inboundFolderId?: string | null;
  inboundFolderName?: string | null;
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
  isSystem: boolean;
  createdUtc: string;
  updatedUtc: string;
};

export type QueueInput = {
  name: string;
  description?: string;
  color?: string;
  icon?: string;
  sortOrder: number;
  isActive: boolean;
  inboundMailboxAddress?: string | null;
  outboundMailboxAddress?: string | null;
  inboundFolderId?: string | null;
  inboundFolderName?: string | null;
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
};

export const STATE_CATEGORIES: StatusStateCategory[] = [
  "New",
  "Open",
  "Pending",
  "Resolved",
  "Closed",
];

// ---- Settings ----

export type SettingEntry = {
  key: string;
  value: string;
  valueType: string;
  category: string;
  description: string;
  defaultValue: string;
  updatedUtc: string;
};

export type NavigationSettings = {
  showOpenTickets: boolean;
};

export type NotificationsSettings = {
  popupDurationSeconds: number;
};

export const settingsApi = {
  list: (category?: string) => {
    const params = category ? `?category=${encodeURIComponent(category)}` : "";
    return request<SettingEntry[]>("GET", `/api/settings${params}`);
  },
  update: (key: string, value: string) =>
    request<void>("PUT", `/api/settings/${encodeURIComponent(key)}`, { value }),
  navigation: () =>
    request<NavigationSettings>("GET", "/api/settings/navigation"),
  notifications: () =>
    request<NotificationsSettings>("GET", "/api/settings/notifications"),
};

// ---- Microsoft Graph admin ----

export type GraphSecretStatus = { configured: boolean };
export type GraphTestResult = { ok: boolean; latencyMs?: number; error?: string };
export type GraphMailFolder = { id: string; displayName: string; totalItemCount: number };

export const graphAdminApi = {
  secretStatus: () =>
    request<GraphSecretStatus>("GET", "/api/admin/settings/graph/secret"),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/settings/graph/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/settings/graph/secret"),
  test: (mailbox: string) =>
    request<GraphTestResult>("POST", "/api/admin/settings/graph/test", { mailbox }),
  listFolders: (mailbox: string) =>
    request<GraphMailFolder[]>("GET", `/api/admin/settings/graph/folders?mailbox=${encodeURIComponent(mailbox)}`),
};

// ---- Adsolut OAuth integration ----

export type AdsolutState =
  | "not_configured"
  | "not_connected"
  | "connected"
  | "refresh_failed";

export type AdsolutStatus = {
  state: AdsolutState;
  environment: string;
  clientIdConfigured: boolean;
  clientSecretConfigured: boolean;
  scopes: string;
  redirectUri: string;
  authorizedSubject: string | null;
  authorizedEmail: string | null;
  authorizedUtc: string | null;
  lastRefreshedUtc: string | null;
  accessTokenExpiresUtc: string | null;
  lastRefreshError: string | null;
  lastRefreshErrorUtc: string | null;
};

export type AdsolutSecretStatus = { configured: boolean };
export type AdsolutAuthorizeStartResponse = { authorizeUrl: string };
export type AdsolutRefreshResult = {
  ok: boolean;
  expiresUtc?: string;
  upstreamErrorCode?: string;
  requiresReconnect?: boolean;
  message?: string;
};

export type IntegrationAuditOutcome = "ok" | "warn" | "error";

export type IntegrationAuditEntry = {
  id: number;
  utc: string;
  eventType: string;
  outcome: IntegrationAuditOutcome;
  endpoint: string | null;
  httpStatus: number | null;
  latencyMs: number | null;
  actorId: string | null;
  actorRole: string | null;
  errorCode: string | null;
  payload: string;
};

export type IntegrationAuditPage = {
  items: IntegrationAuditEntry[];
  nextCursor: number | null;
};

export const adsolutApi = {
  status: () =>
    request<AdsolutStatus>("GET", "/api/admin/integrations/adsolut/status"),
  startAuthorize: () =>
    request<AdsolutAuthorizeStartResponse>(
      "POST",
      "/api/admin/integrations/adsolut/authorize",
    ),
  disconnect: () =>
    request<void>("POST", "/api/admin/integrations/adsolut/disconnect"),
  refresh: () =>
    request<AdsolutRefreshResult>(
      "POST",
      "/api/admin/integrations/adsolut/refresh",
    ),
  secretStatus: () =>
    request<AdsolutSecretStatus>(
      "GET",
      "/api/admin/integrations/adsolut/secret",
    ),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/integrations/adsolut/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/integrations/adsolut/secret"),
  auditLog: (cursor: number | null, limit = 50) => {
    const qs = new URLSearchParams();
    if (cursor !== null) qs.set("cursor", String(cursor));
    qs.set("limit", String(limit));
    return request<IntegrationAuditPage>(
      "GET",
      `/api/admin/integrations/adsolut/audit?${qs.toString()}`,
    );
  },
};

// ---- Mail attachment diagnostics ----

export type MailAttachmentJobDiagnostic = {
  jobId: number;
  state: string;
  attemptCount: number;
  nextAttemptUtc: string;
  lastError?: string | null;
  updatedUtc: string;
  attachmentId: string;
};

export type MailAttachmentDiagnosticItem = {
  id: string;
  filename: string;
  mimeType: string;
  sizeBytes: number;
  isInline: boolean;
  contentId?: string | null;
  contentHash?: string | null;
  processingState: string;
  createdUtc: string;
  blobPresent: boolean;
  job?: MailAttachmentJobDiagnostic | null;
};

export type MailAttachmentDiagnostic = {
  mailMessageId: string;
  ticketId?: string | null;
  subject?: string | null;
  fromAddress?: string | null;
  receivedUtc: string;
  bodyHtmlBlobHash?: string | null;
  bodyHtmlBlobPresent: boolean;
  attachments: MailAttachmentDiagnosticItem[];
};

export type MailAttachmentSummary = {
  mailMessageId: string;
  ticketId?: string | null;
  subject?: string | null;
  fromAddress?: string | null;
  receivedUtc: string;
  attachmentTotal: number;
  readyCount: number;
  pendingCount: number;
  failedCount: number;
};

export const mailDiagnosticsApi = {
  list: (onlyIssues: boolean, limit = 25) =>
    request<MailAttachmentSummary[]>(
      "GET",
      `/api/admin/mail/diagnostics?limit=${limit}&onlyIssues=${onlyIssues ? "true" : "false"}`,
    ),
  get: (mailMessageId: string) =>
    request<MailAttachmentDiagnostic>("GET", `/api/admin/mail/diagnostics/${mailMessageId}`),
};

// ---- Agent-Facing Queue List (scoped to accessible queues) ----

export const agentQueueApi = {
  list: () => request<Queue[]>("GET", "/api/queues"),
};

// ---- Queue Access Admin ----

export const queueAccessApi = {
  getForUser: (userId: string) =>
    request<{ queueIds: string[] }>("GET", `/api/admin/queue-access/${userId}`),
  setForUser: (userId: string, queueIds: string[]) =>
    request<void>("PUT", `/api/admin/queue-access/${userId}`, { queueIds }),
  getByQueue: (queueId: string) =>
    request<{ userIds: string[] }>("GET", `/api/admin/queue-access/by-queue/${queueId}`),
};

// ---- View Groups Admin ----

export type ViewGroupSummary = {
  id: string;
  name: string;
  description: string;
  sortOrder: number;
  memberCount: number;
  viewCount: number;
  createdUtc: string;
  updatedUtc: string;
};

export type ViewGroupMember = {
  userId: string;
  email: string;
};

export type ViewGroupView = {
  viewId: string;
  viewName: string;
  sortOrder: number;
};

export type ViewGroupDetail = {
  group: ViewGroupSummary;
  members: ViewGroupMember[];
  views: ViewGroupView[];
};

export type ViewGroupInput = {
  name: string;
  description?: string;
  sortOrder?: number;
};

export const viewGroupApi = {
  list: () => request<ViewGroupSummary[]>("GET", "/api/admin/view-groups"),
  get: (id: string) => request<ViewGroupDetail>("GET", `/api/admin/view-groups/${id}`),
  create: (input: ViewGroupInput) =>
    request<ViewGroupSummary>("POST", "/api/admin/view-groups", input),
  update: (id: string, input: ViewGroupInput) =>
    request<ViewGroupSummary>("PUT", `/api/admin/view-groups/${id}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/admin/view-groups/${id}`),
  setMembers: (id: string, userIds: string[]) =>
    request<void>("PUT", `/api/admin/view-groups/${id}/members`, { userIds }),
  setViews: (id: string, viewIds: string[]) =>
    request<void>("PUT", `/api/admin/view-groups/${id}/views`, { viewIds }),
};

// ---- View Access Admin (direct assignment) ----

export const viewAccessApi = {
  getForUser: (userId: string) =>
    request<{ viewIds: string[] }>("GET", `/api/admin/view-access/${userId}`),
  setForUser: (userId: string, viewIds: string[]) =>
    request<void>("PUT", `/api/admin/view-access/${userId}`, { viewIds }),
};

// ---- User Preferences ----

export type ColumnPreference = {
  columns: string;
  source: "user-view" | "view" | "user" | "default";
};

export type WorkspaceEntryDto = { key: string; value: string };

export const preferencesApi = {
  getColumns: (viewId?: string) => {
    const qs = viewId ? `?viewId=${viewId}` : "";
    return request<ColumnPreference>("GET", `/api/preferences/columns${qs}`);
  },
  saveColumns: (columns: string, viewId?: string) => {
    const qs = viewId ? `?viewId=${viewId}` : "";
    return request<void>("PUT", `/api/preferences/columns${qs}`, { columns });
  },
  resetColumns: (viewId?: string) => {
    const qs = viewId ? `?viewId=${viewId}` : "";
    return request<void>("DELETE", `/api/preferences/columns${qs}`);
  },
  getWorkspace: () =>
    request<Record<string, string>>("GET", "/api/preferences/workspace"),
  saveWorkspace: (entries: WorkspaceEntryDto[]) =>
    request<void>("PUT", "/api/preferences/workspace", { entries }),
  deleteWorkspaceKey: (key: string) =>
    request<void>(
      "DELETE",
      `/api/preferences/workspace/${encodeURIComponent(key)}`,
    ),
  /** Fire-and-forget save using keepalive — survives page unload. */
  fireAndForgetWorkspaceSave: (entries: WorkspaceEntryDto[]) => {
    fetch("/api/preferences/workspace", {
      method: "PUT",
      credentials: "include",
      keepalive: true,
      headers: {
        "Content-Type": "application/json",
        ...csrfHeader(),
      },
      body: JSON.stringify({ entries }),
    });
  },
};

// ---- SLA ----

export type BusinessHoursSlot = {
  id: number;
  schemaId: string;
  dayOfWeek: number;
  startMinute: number;
  endMinute: number;
};

export type Holiday = {
  id: number;
  schemaId: string;
  date: string;
  name: string;
  source: "manual" | "nager";
  countryCode: string;
};

export type BusinessHoursSchema = {
  id: string;
  name: string;
  timezone: string;
  countryCode: string;
  isDefault: boolean;
  slots: BusinessHoursSlot[];
  holidays: Holiday[];
};

export type SlaPolicy = {
  id: string;
  queueId: string | null;
  priorityId: string;
  businessHoursSchemaId: string;
  firstResponseMinutes: number | null;
  resolutionMinutes: number | null;
  pauseOnPending: boolean;
};

export type TicketSlaState = {
  ticketId: string;
  policyId: string | null;
  firstResponseDeadlineUtc: string | null;
  resolutionDeadlineUtc: string | null;
  firstResponseMetUtc: string | null;
  resolutionMetUtc: string | null;
  firstResponseBusinessMinutes: number | null;
  resolutionBusinessMinutes: number | null;
  isPaused: boolean;
  pausedSinceUtc: string | null;
  pausedAccumMinutes: number;
  lastRecalcUtc: string;
  updatedUtc: string;
};

export type SlaLogItem = {
  ticketId: string;
  number: number;
  subject: string;
  queueId: string;
  queueName: string;
  priorityId: string;
  priorityName: string;
  statusId: string;
  statusName: string;
  createdUtc: string;
  firstResponseDeadlineUtc: string | null;
  firstResponseMetUtc: string | null;
  resolutionDeadlineUtc: string | null;
  resolutionMetUtc: string | null;
  firstResponseTargetMinutes: number | null;
  resolutionTargetMinutes: number | null;
  firstResponseBusinessMinutes: number | null;
  resolutionBusinessMinutes: number | null;
  isPaused: boolean;
  firstResponseBreached: boolean;
  resolutionBreached: boolean;
};

export type SlaLogPage = { items: SlaLogItem[]; nextCursor: number | null };

export type QueueAvgPickup = {
  queueId: string;
  queueName: string;
  ticketCount: number;
  avgBusinessMinutes: number | null;
};

export type NagerCountry = { countryCode: string; name: string };

export const slaApi = {
  listSchemas: () => request<BusinessHoursSchema[]>("GET", "/api/sla/business-hours"),
  createSchema: (body: unknown) =>
    request<BusinessHoursSchema>("POST", "/api/sla/business-hours", body),
  updateSchema: (id: string, body: unknown) =>
    request<BusinessHoursSchema>("PUT", `/api/sla/business-hours/${id}`, body),
  deleteSchema: (id: string) => request<void>("DELETE", `/api/sla/business-hours/${id}`),

  listHolidays: (schemaId: string, year?: number) => {
    const qs = year ? `?year=${year}` : "";
    return request<Holiday[]>("GET", `/api/sla/business-hours/${schemaId}/holidays${qs}`);
  },
  addHoliday: (schemaId: string, body: { date: string; name?: string; countryCode?: string }) =>
    request<void>("POST", `/api/sla/business-hours/${schemaId}/holidays`, body),
  deleteHoliday: (id: number) => request<void>("DELETE", `/api/sla/holidays/${id}`),
  syncHolidays: (schemaId: string, countryCode: string, year?: number) =>
    request<void>("POST", `/api/sla/business-hours/${schemaId}/holidays/sync`, { countryCode, year }),
  listCountries: () => request<NagerCountry[]>("GET", "/api/sla/holidays/countries"),

  listPolicies: () => request<SlaPolicy[]>("GET", "/api/sla/policies"),
  upsertPolicy: (body: unknown) => request<SlaPolicy>("PUT", "/api/sla/policies", body),
  deletePolicy: (id: string) => request<void>("DELETE", `/api/sla/policies/${id}`),

  log: (params: {
    queueId?: string;
    priorityId?: string;
    statusId?: string;
    breachedOnly?: boolean;
    fromUtc?: string;
    toUtc?: string;
    search?: string;
    cursorNumber?: number;
    limit?: number;
  }) => {
    const qs = new URLSearchParams();
    if (params.queueId) qs.set("queueId", params.queueId);
    if (params.priorityId) qs.set("priorityId", params.priorityId);
    if (params.statusId) qs.set("statusId", params.statusId);
    if (params.breachedOnly) qs.set("breachedOnly", "true");
    if (params.fromUtc) qs.set("fromUtc", params.fromUtc);
    if (params.toUtc) qs.set("toUtc", params.toUtc);
    if (params.search) qs.set("search", params.search);
    if (params.cursorNumber) qs.set("cursorNumber", String(params.cursorNumber));
    qs.set("limit", String(params.limit ?? 50));
    return request<SlaLogPage>("GET", `/api/sla/log?${qs.toString()}`);
  },

  avgPickup: (days = 7) =>
    request<{ days: number; items: QueueAvgPickup[] }>(
      "GET",
      `/api/sla/dashboard/avg-pickup?days=${days}`,
    ),

  ticketState: (ticketId: string) =>
    request<TicketSlaState | null>("GET", `/api/sla/tickets/${ticketId}`),
};

// ---- Global Search ----

export type SearchHit = {
  kind: string;
  entityId: string;
  title: string;
  snippet: string | null;
  rank: number;
  meta: Record<string, string | null> | null;
};

export type SearchGroup = {
  kind: string;
  hits: SearchHit[];
  totalInGroup: number;
  hasMore: boolean;
};

export type SearchDropdownResponse = {
  groups: SearchGroup[];
  totalHits: number;
  availableKinds: string[];
  minQueryLength: number;
};

export type SearchFullResponse = {
  group: SearchGroup;
  availableKinds: string[];
  minQueryLength: number;
};

export const searchApi = {
  quick: (q: string, limit = 8) =>
    request<SearchDropdownResponse>(
      "GET",
      `/api/search?q=${encodeURIComponent(q)}&limit=${limit}`,
    ),
  full: (q: string, type: string, limit = 25, offset = 0) =>
    request<SearchFullResponse>(
      "GET",
      `/api/search/full?q=${encodeURIComponent(q)}&type=${encodeURIComponent(type)}&limit=${limit}&offset=${offset}`,
    ),
};

// ---- Triggers (admin) ----

export type TriggerActivatorKind = "action" | "time";
export type TriggerActivatorMode =
  | "selective"
  | "always"
  | "reminder"
  | "escalation"
  | "escalation_warning";

export type TriggerRunSummary = {
  applied: number;
  skippedNoMatch: number;
  skippedLoop: number;
  failed: number;
  lastFiredUtc: string | null;
};

export type TriggerListItem = {
  id: string;
  name: string;
  description: string;
  isActive: boolean;
  activatorKind: TriggerActivatorKind;
  activatorMode: TriggerActivatorMode;
  locale: string | null;
  timezone: string | null;
  createdUtc: string;
  updatedUtc: string;
  runs: TriggerRunSummary;
};

export type TriggerListResponse = {
  items: TriggerListItem[];
  runSummaryWindowHours: number;
};

export type TriggerDetail = {
  id: string;
  name: string;
  description: string;
  isActive: boolean;
  activatorKind: TriggerActivatorKind;
  activatorMode: TriggerActivatorMode;
  conditionsJson: string;
  actionsJson: string;
  locale: string | null;
  timezone: string | null;
  note: string;
  createdUtc: string;
  updatedUtc: string;
  createdByUserId: string | null;
};

export type TriggerInput = {
  name: string;
  description: string;
  isActive: boolean;
  activatorKind: TriggerActivatorKind;
  activatorMode: TriggerActivatorMode;
  conditionsJson: string;
  actionsJson: string;
  locale: string | null;
  timezone: string | null;
  note: string;
};

export type TriggerConditionField = {
  key: string;
  label: string;
  type: string;
};

export type TriggerTemplateVariable = {
  path: string;
  label: string;
  type: "string" | "datetime";
  example: string;
};

export type TriggerMetadata = {
  conditionFields: TriggerConditionField[];
  conditionOperators: string[];
  actionKinds: string[];
  activatorPairs: string[];
  templateVariables: TriggerTemplateVariable[];
  maxConditionDepth: number;
};

export type TriggerRun = {
  id: string;
  triggerId: string;
  ticketId: string;
  ticketNumber: number | null;
  ticketEventId: number | null;
  firedUtc: string;
  outcome: string;
  appliedChangesJson: string | null;
  errorClass: string | null;
  errorMessage: string | null;
};

export type TriggerRunPage = {
  items: TriggerRun[];
  nextCursor: string | null;
};

export type TriggerDryRunActionStatus =
  | "wouldapply"
  | "wouldnoop"
  | "failed"
  | "nohandler";

export type TriggerDryRunAction = {
  kind: string;
  status: TriggerDryRunActionStatus;
  summary: unknown;
  failure: string | null;
};

export type TriggerDryRunResult = {
  matched: boolean;
  failureReason: string | null;
  actions: TriggerDryRunAction[];
};

export const triggerApi = {
  list: () => request<TriggerListResponse>("GET", "/api/admin/triggers"),
  get: (id: string) => request<TriggerDetail>("GET", `/api/admin/triggers/${id}`),
  create: (body: TriggerInput) =>
    request<TriggerDetail>("POST", "/api/admin/triggers", body),
  update: (id: string, body: TriggerInput) =>
    request<TriggerDetail>("PUT", `/api/admin/triggers/${id}`, body),
  setActive: (id: string, isActive: boolean) =>
    request<void>("POST", `/api/admin/triggers/${id}/active`, { isActive }),
  remove: (id: string) =>
    request<void>("DELETE", `/api/admin/triggers/${id}`),
  metadata: () => request<TriggerMetadata>("GET", "/api/admin/triggers/metadata"),
  runs: (id: string, params: { limit?: number; cursorUtc?: string } = {}) => {
    const qs = new URLSearchParams();
    if (params.limit) qs.set("limit", String(params.limit));
    if (params.cursorUtc) qs.set("cursorUtc", params.cursorUtc);
    const tail = qs.toString() ? `?${qs.toString()}` : "";
    return request<TriggerRunPage>("GET", `/api/admin/triggers/${id}/runs${tail}`);
  },
  dryRun: (id: string, ticketId: string) =>
    request<TriggerDryRunResult>("POST", `/api/admin/triggers/${id}/dry-run`, { ticketId }),
};

export type AuthConfig = {
  microsoftEnabled: boolean;
  setupAvailable: boolean;
};

export const authApi = {
  setupStatus: () => request<SetupStatus>("GET", "/api/auth/setup/status"),
  config: () => request<AuthConfig>("GET", "/api/auth/config"),
  createAdmin: (email: string, password: string) =>
    request<{ email: string; role: Role }>("POST", "/api/auth/setup/create-admin", { email, password }),
  login: (email: string, password: string) =>
    request<LoginResponse>("POST", "/api/auth/login", { email, password }),
  verifyTwoFactor: (code: string) =>
    request<void>("POST", "/api/auth/2fa/verify", { code }),
  logout: () => request<void>("POST", "/api/auth/logout"),
  me: () => request<MeResponse>("GET", "/api/auth/me"),
  beginTotpEnroll: () => request<TotpEnrollment>("POST", "/api/auth/2fa/enroll/begin"),
  confirmTotpEnroll: (code: string) =>
    request<RecoveryCodesResponse>("POST", "/api/auth/2fa/enroll/confirm", { code }),
  disableTotp: () => request<void>("POST", "/api/auth/2fa/disable"),
};
