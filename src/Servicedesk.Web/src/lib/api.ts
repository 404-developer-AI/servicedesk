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

export const settingsApi = {
  list: (category?: string) => {
    const params = category ? `?category=${encodeURIComponent(category)}` : "";
    return request<SettingEntry[]>("GET", `/api/settings${params}`);
  },
  update: (key: string, value: string) =>
    request<void>("PUT", `/api/settings/${encodeURIComponent(key)}`, { value }),
  navigation: () =>
    request<NavigationSettings>("GET", "/api/settings/navigation"),
};

// ---- Microsoft Graph admin ----

export type GraphSecretStatus = { configured: boolean };
export type GraphTestResult = { ok: boolean; latencyMs?: number; error?: string };

export const graphAdminApi = {
  secretStatus: () =>
    request<GraphSecretStatus>("GET", "/api/admin/settings/graph/secret"),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/settings/graph/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/settings/graph/secret"),
  test: (mailbox: string) =>
    request<GraphTestResult>("POST", "/api/admin/settings/graph/test", { mailbox }),
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

export const authApi = {
  setupStatus: () => request<SetupStatus>("GET", "/api/auth/setup/status"),
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
