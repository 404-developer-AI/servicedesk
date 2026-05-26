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

export type LoginBannerType = "info" | "warning" | "error";

export type LoginBannerState = {
  enabled: boolean;
  type: LoginBannerType;
  message: string;
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
  timesheetEnabled: boolean;
  timesheetManager: boolean;
  isIsoMgm: boolean;
  isIsoDpo: boolean;
  kbEnabled: boolean;
  searchEnabled: boolean;
  dashboardTiles: string[];
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
  /// Parsed JSON body of the error response, when present. Most endpoints
  /// return `{ error, message, ... }` on non-2xx; surfacing it here lets
  /// callers show the upstream detail without each one re-reading the
  /// response stream (which is already consumed by the time the error is
  /// thrown). `null` when the body was empty / not JSON.
  readonly body: unknown;
  constructor(status: number, url: string, message: string, body: unknown = null) {
    super(message);
    this.status = status;
    this.url = url;
    this.body = body;
  }
}

/// Pulls a human-readable message out of an ApiError body if the server
/// returned `{ message: "..." }` or `{ error: "..." }`. Returns `null`
/// when neither is present — callers fall back to the generic
/// "Operation failed (HTTP N)" string in that case.
export function apiErrorMessage(err: unknown): string | null {
  if (!(err instanceof ApiError) || err.body === null || typeof err.body !== "object") {
    return null;
  }
  const b = err.body as Record<string, unknown>;
  if (typeof b.message === "string" && b.message.length > 0) return b.message;
  if (typeof b.error === "string" && b.error.length > 0) return b.error;
  return null;
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
    // Best-effort: read the body so callers can show upstream error
    // detail (Telavox 502 envelopes carry the actual PAPI error code +
    // message under `{ message, upstreamErrorCode, httpStatus }`).
    // Parse failures are silently dropped — the ApiError still carries
    // status + url so the generic fallback path works.
    let parsed: unknown = null;
    try {
      const text = await res.text();
      if (text.length > 0) parsed = JSON.parse(text);
    } catch {
      // ignore — body wasn't JSON
    }
    throw new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`, parsed);
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

export type IntegrationHealth = {
  key: string;
  name: string;
  logoKey: string;
  status: HealthStatus;
  checks: SubsystemHealth[];
  /// Tile-level actions (e.g. Sync now) — distinct from per-check actions.
  /// The dashboard tile renders these centred next to the integration row.
  actions: HealthAction[];
};

export type IntegrationsHealthReport = {
  status: HealthStatus;
  integrations: IntegrationHealth[];
};

export const systemApi = {
  version: () => request<SystemVersion>("GET", "/api/system/version"),
  time: () => request<SystemTime>("GET", "/api/system/time"),
  health: () => request<{ status: HealthStatus }>("GET", "/api/system/health"),
  maintenance: () => request<MaintenanceState>("GET", "/api/system/maintenance"),
  loginBanner: () => request<LoginBannerState>("GET", "/api/system/login-banner"),
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

export const integrationsHealthApi = {
  get: () =>
    request<IntegrationsHealthReport>("GET", "/api/admin/health/integrations"),
};

export type AgentActivityTicket = {
  id: string;
  number: number;
  subject: string;
  statusName: string;
  statusColor: string;
  statusStateCategory: string;
};

/// "idle" — no Telavox call (default for users without a linked
/// extension). "ringing" — phone is alerting. "answered" — agent
/// picked up. Drives the indicator on the AgentActivity tile (green
/// dot vs. red phone).
export type AgentCallState = "idle" | "ringing" | "answered";

export type AgentActivity = {
  userId: string;
  email: string;
  roleName: string;
  online: boolean;
  callState: AgentCallState;
  viewing: AgentActivityTicket | null;
  recent: AgentActivityTicket[];
};

export type AgentActivitySnapshot = {
  agents: AgentActivity[];
};

export const dashboardApi = {
  agentActivity: () =>
    request<AgentActivitySnapshot>("GET", "/api/dashboard/agent-activity"),
};

// ---- Activity feed (v0.0.42) ----

/// One row in the agent activity feed. Mirrors the backend
/// ActivityFeedEntry record. EntityId is the source entity's
/// stringified id (ticket UUID, settings key, user id, …); the UI
/// uses entityType to decide whether to render it as a link.
export type ActivityEntry = {
  id: number;
  occurredUtc: string;
  agentId: string;
  agentEmail: string;
  agentRole: string;
  eventType: string;
  entityType: string | null;
  entityId: string | null;
  entityExtra: string | null;
  summary: string;
  metadataJson: string;
  /// Enriched at query time for ticket-typed events. Null for any
  /// other entity type.
  ticketNumber: number | null;
  ticketSubject: string | null;
};

export type ActivityListQuery = {
  agentId?: string;
  eventType?: string;
  fromUtc?: string;
  toUtc?: string;
  search?: string;
  cursor?: number;
  limit?: number;
};

export type ActivityListPage = {
  items: ActivityEntry[];
  nextCursor: number | null;
};

// ---- Recent tickets (v0.0.42) — server-side per-user sidebar list ----

export type RecentTicketEntry = {
  id: string;
  number: number;
  subject: string;
  statusColor?: string;
  statusName?: string;
  statusStateCategory?: string;
};

export const recentTicketsApi = {
  list: () => request<{ items: RecentTicketEntry[] }>("GET", "/api/me/recent-tickets/"),
  add: (ticketId: string) =>
    request<void>("POST", `/api/me/recent-tickets/${ticketId}`),
  remove: (ticketId: string) =>
    request<void>("DELETE", `/api/me/recent-tickets/${ticketId}`),
  reorder: (ticketIds: string[]) =>
    request<void>("PUT", "/api/me/recent-tickets/order", { ticketIds }),
};

// ---- User dashboard layout (v0.0.42) ----

/// One row in the user's saved dashboard layout. Mirrors the backend
/// DashboardTilePref record.
export type DashboardLayoutEntry = {
  tileId: string;
  size: "small" | "medium" | "wide" | "full";
};

export const userDashboardLayoutApi = {
  /// Replace the signed-in user's full layout (order + size per tile).
  /// Returns the persisted layout for cache reconciliation.
  save: (tiles: DashboardLayoutEntry[]) =>
    request<{ tiles: DashboardLayoutEntry[] }>(
      "PUT",
      "/api/me/dashboard/layout",
      { tiles },
    ),
};

export const activityApi = {
  recent: (limit = 25) =>
    request<{ items: ActivityEntry[] }>(
      "GET",
      `/api/activity/recent?limit=${limit}`,
    ),
  list: (query: ActivityListQuery = {}) => {
    const params = new URLSearchParams();
    if (query.agentId) params.set("agentId", query.agentId);
    if (query.eventType) params.set("eventType", query.eventType);
    if (query.fromUtc) params.set("fromUtc", query.fromUtc);
    if (query.toUtc) params.set("toUtc", query.toUtc);
    if (query.search) params.set("search", query.search);
    if (query.cursor !== undefined) params.set("cursor", String(query.cursor));
    params.set("limit", String(query.limit ?? 50));
    return request<ActivityListPage>(
      "GET",
      `/api/activity?${params.toString()}`,
    );
  },
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
  // v0.0.40 polish — per-queue status scope. Empty array = all
  // statuses available. Non-empty = dropdown filters to these ids.
  allowedStatusIds: string[];
  defaultStatusId: string | null;
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
  // v0.0.40 polish — per-queue status scope. Empty / null = current
  // behaviour (all statuses); non-empty = filter list. defaultStatusId
  // drives the auto-flip on queue change.
  allowedStatusIds?: string[];
  defaultStatusId?: string | null;
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
  | "refresh_failed"
  // OAuth healthy but the most recent sync.tick recorded an error. UI shows
  // an amber "Sync failing" pill and points the admin at the audit log; no
  // reconnect is required (different from refresh_failed).
  | "sync_failing";

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
  administrationId: string | null;
  /// Scopes string that was bound to the current refresh token at authorize
  /// time. Null when the install is not connected.
  scopesAtAuthorize: string | null;
  /// True when the saved Settings.Adsolut.Scopes value differs from the
  /// scope set on the active refresh token — admin saved new scopes via
  /// the picker but did not reconnect yet, so the next API call will fail
  /// with insufficient_scope. Surface a "Reconnect required" pill.
  scopesNeedReconnect: boolean;
};

export type AdsolutAdministration = {
  id: string;
  name: string;
  code: string | null;
};

export type AdsolutAdministrationsResponse = {
  items: AdsolutAdministration[];
  selectedId: string | null;
};

export type AdsolutSyncState = {
  lastFullSyncUtc: string | null;
  lastDeltaSyncUtc: string | null;
  lastError: string | null;
  lastErrorUtc: string | null;
  companiesSeen: number;
  companiesUpserted: number;
  companiesSkippedLoserInConflict: number;
  updatedUtc: string | null;
  // Estimated wall-clock for the next worker tick. Computed server-side
  // from lastDeltaSyncUtc + intervalMinutes — never trust the client clock.
  nextSyncUtc: string | null;
  intervalMinutes: number;
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

export type AdsolutDebugKind = "customer" | "supplier";

export type AdsolutDebugLookupResponse = {
  status: number;
  requestUrl: string;
  upstreamErrorCode: string | null;
  body: string;
};

export type AdsolutDebugPutPreview = {
  getStatus: number;
  getRequestUrl: string;
  upstreamErrorCode: string | null;
  getBody: string;
  putUrl: string;
  putBody: string | null;
  putBuildError: string | null;
};

export type AdsolutDebugPutResponse = {
  status: number;
  requestUrl: string;
  upstreamErrorCode: string | null;
  body: string;
};

export type AdsolutDebugAccessToken = {
  accessToken: string;
  accessTokenExpiresUtc: string | null;
  baseUrl: string;
  administrationId: string | null;
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
  listAdministrations: () =>
    request<AdsolutAdministrationsResponse>(
      "GET",
      "/api/admin/integrations/adsolut/administrations",
    ),
  selectAdministration: (administrationId: string) =>
    request<{ administrationId: string }>(
      "POST",
      "/api/admin/integrations/adsolut/administration",
      { administrationId },
    ),
  syncState: () =>
    request<AdsolutSyncState>("GET", "/api/admin/integrations/adsolut/sync"),
  triggerSync: () =>
    request<void>("POST", "/api/admin/integrations/adsolut/sync"),
  debugLookup: (kind: AdsolutDebugKind, code: string) => {
    const qs = new URLSearchParams({ kind, code });
    return request<AdsolutDebugLookupResponse>(
      "GET",
      `/api/admin/integrations/adsolut/debug/lookup?${qs.toString()}`,
    );
  },
  debugPutPreview: (customerId: string) => {
    const qs = new URLSearchParams({ customerId });
    return request<AdsolutDebugPutPreview>(
      "GET",
      `/api/admin/integrations/adsolut/debug/put-preview?${qs.toString()}`,
    );
  },
  debugCustomerPut: (customerId: string, body: string) =>
    request<AdsolutDebugPutResponse>(
      "POST",
      "/api/admin/integrations/adsolut/debug/put",
      { customerId, body },
    ),
  debugAccessToken: () =>
    request<AdsolutDebugAccessToken>(
      "GET",
      "/api/admin/integrations/adsolut/debug/access-token",
    ),
  /// v0.0.28 — manual contacts resync for one company. Returns the per-tick
  /// counters from the reconciler so the UI can show "Synced 4 contacts,
  /// 1 created, 0 reconciled" inline. Returns null when the company is not
  /// linked to Adsolut, the integration isn't connected, or no dossier is
  /// selected (server returns 404 in those cases — the API helper rethrows
  /// as ApiError so the caller can handle it).
  resyncCompanyContacts: (companyId: string) =>
    request<AdsolutContactsResyncResult>(
      "POST",
      `/api/admin/integrations/adsolut/companies/${companyId}/resync-contacts`,
    ),
  /// v0.0.30 — Sync coverage tile + page.
  coverageCounts: () =>
    request<AdsolutCoverageCounts>(
      "GET",
      "/api/admin/integrations/adsolut/coverage",
    ),
  coverageCompanies: (
    bucket: AdsolutCoverageCompaniesBucket,
    search: string,
    page: number,
    pageSize: number,
  ) => {
    const qs = new URLSearchParams({
      bucket,
      page: String(page),
      pageSize: String(pageSize),
    });
    if (search.trim().length > 0) qs.set("search", search.trim());
    return request<AdsolutCoveragePage<AdsolutCoverageCompanyRow>>(
      "GET",
      `/api/admin/integrations/adsolut/coverage/companies?${qs.toString()}`,
    );
  },
  coverageContacts: (
    bucket: AdsolutCoverageContactsBucket,
    search: string,
    page: number,
    pageSize: number,
  ) => {
    const qs = new URLSearchParams({
      bucket,
      page: String(page),
      pageSize: String(pageSize),
    });
    if (search.trim().length > 0) qs.set("search", search.trim());
    return request<AdsolutCoveragePage<AdsolutCoverageContactRow>>(
      "GET",
      `/api/admin/integrations/adsolut/coverage/contacts?${qs.toString()}`,
    );
  },
  coverageLinkCompany: (companyId: string, adsolutId: string, lastModified: string | null) =>
    request<{ ok: boolean }>(
      "POST",
      `/api/admin/integrations/adsolut/coverage/link/company/${companyId}`,
      { adsolutId, lastModified },
    ),
  coverageLinkContact: (linkId: string, adsolutContactId: string, lastModified: string | null) =>
    request<{ ok: boolean }>(
      "POST",
      `/api/admin/integrations/adsolut/coverage/link/contact/${linkId}`,
      { adsolutContactId, lastModified },
    ),
  coverageForcePushCompany: (companyId: string) =>
    request<{ outcome: string }>(
      "POST",
      `/api/admin/integrations/adsolut/coverage/force-push/company/${companyId}`,
    ),
  coverageForcePushContact: (linkId: string) =>
    request<{ outcome: string }>(
      "POST",
      `/api/admin/integrations/adsolut/coverage/force-push/contact/${linkId}`,
    ),
};

export type AdsolutCoverageCounts = {
  companiesSdOnly: number;
  companiesDrift: number;
  contactLinksUnsynced: number;
  contactLinksDrift: number;
  contactsPureSd: number;
};

export type AdsolutCoverageCompaniesBucket = "sd-only" | "drift";
export type AdsolutCoverageContactsBucket =
  | "links-unsynced"
  | "links-drift"
  | "pure-sd";

export type AdsolutCoverageCompanyRow = {
  id: string;
  name: string;
  code: string | null;
  email: string | null;
  adsolutId: string | null;
  adsolutNumber: string | null;
  adsolutLastModified: string | null;
  updatedUtc: string;
};

export type AdsolutCoverageContactRow = {
  linkId: string | null;
  contactId: string;
  email: string;
  firstName: string;
  lastName: string;
  companyId: string | null;
  companyName: string | null;
  companyAdsolutId: string | null;
  adsolutContactId: string | null;
  adsolutLastModified: string | null;
  contactUpdatedUtc: string;
};

export type AdsolutCoveragePage<T> = {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
};

export type AdsolutContactsResyncResult = {
  companyId: string;
  pullUpdate: boolean;
  pullCreate: boolean;
  companiesScanned: number;
  contactsSeen: number;
  contactsCreated: number;
  contactsUpdated: number;
  contactsSkippedNoChange: number;
  contactsSkippedLocalNewer: number;
  contactsSkippedToggleOff: number;
  contactsSkippedNoEmail: number;
  contactsSkippedLinkConflict: number;
  linksReconciled: number;
};

// ---- Telavox call-popup integration (v0.0.34) ----

export type TelavoxConnectionState =
  | "Disabled"
  | "NotConfigured"
  | "NoCustomerSelected"
  | "Ready"
  | "Active";

export type TelavoxStatus = {
  state: TelavoxConnectionState;
  enabled: boolean;
  partnerTokenConfigured: boolean;
  partnerCustomerId: string | null;
  linkedAgentCount: number;
};

export type TelavoxCustomer = { id: string; name: string };

export type TelavoxExtension = {
  id: string;
  number: string;
  name: string;
  userEmail: string | null;
};

export type TelavoxExtensionsResponse = {
  customerId: string;
  items: TelavoxExtension[];
};

export type TelavoxAgentLink = {
  userId: string;
  userEmail: string;
  userRole: string;
  telavoxExtension: string;
  telavoxUserId: string;
  /// PAPI api-user "name" the provisioning service minted for this agent.
  /// Pre-D this field was called `capiUserEmail`; the PAPI swagger takes a
  /// `?name=...` query param, not an email body, so the conceptual rename
  /// followed in v0.0.34 post-D. The DB column is still
  /// `telavox_agent_links.capi_user_email` for backwards compat.
  capiUserName: string;
  provisionedUtc: string;
  lastPollUtc: string | null;
  lastPollError: string | null;
  consecutiveErrors: number;
};

export type TelavoxSecretStatus = { configured: boolean };

export const telavoxAdminApi = {
  status: () =>
    request<TelavoxStatus>("GET", "/api/admin/integrations/telavox/status"),
  secretStatus: () =>
    request<TelavoxSecretStatus>("GET", "/api/admin/integrations/telavox/secret"),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/integrations/telavox/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/integrations/telavox/secret"),
  setCustomerId: (customerId: string) =>
    request<{ customerId: string }>(
      "PUT",
      "/api/admin/integrations/telavox/customer",
      { customerId },
    ),
  clearCustomerId: () =>
    request<void>("DELETE", "/api/admin/integrations/telavox/customer"),
  testConnection: () =>
    request<{ customers: TelavoxCustomer[] }>(
      "POST",
      "/api/admin/integrations/telavox/test-connection",
    ),
  listExtensions: () =>
    request<TelavoxExtensionsResponse>(
      "GET",
      "/api/admin/integrations/telavox/extensions",
    ),
  listAgentLinks: () =>
    request<{ items: TelavoxAgentLink[] }>(
      "GET",
      "/api/admin/integrations/telavox/agents",
    ),
  provisionAgent: (userId: string, extension: string) =>
    request<TelavoxAgentLink>(
      "POST",
      `/api/admin/integrations/telavox/agents/${userId}/provision`,
      { extension },
    ),
  /// Manual-link fallback for installs where PAPI api-user creation is
  /// unavailable. Admin pastes a CAPI bearer-token they minted via the
  /// Telavox webapp; SD stores it verbatim and uses it for per-agent
  /// polling. No upstream call to mint or revoke.
  provisionAgentManual: (userId: string, extension: string, capiToken: string) =>
    request<TelavoxAgentLink>(
      "POST",
      `/api/admin/integrations/telavox/agents/${userId}/provision-manual`,
      { extension, capiToken },
    ),
  revokeAgent: (userId: string) =>
    request<void>(
      "DELETE",
      `/api/admin/integrations/telavox/agents/${userId}/provision`,
    ),
  auditLog: (cursor: number | null, limit = 50) => {
    const qs = new URLSearchParams();
    if (cursor !== null) qs.set("cursor", String(cursor));
    qs.set("limit", String(limit));
    return request<IntegrationAuditPage>(
      "GET",
      `/api/admin/integrations/telavox/audit?${qs.toString()}`,
    );
  },
};

// ---- Zammad migration link (v0.0.41) ----

export type ZammadConnectionState =
  | "Disabled"
  | "NotConfigured"
  | "Ready";

export type ZammadStatus = {
  state: ZammadConnectionState;
  enabled: boolean;
  tokenConfigured: boolean;
  baseUrl: string | null;
};

export type ZammadSecretStatus = { configured: boolean };

export type ZammadMe = {
  id: number;
  email: string | null;
  firstName: string | null;
  lastName: string | null;
  login: string | null;
};

export type ZammadTestResult = {
  me: ZammadMe;
  version: string | null;
  latencyMs: number;
};

export type ZammadGroup = {
  id: number;
  name: string;
  active: boolean;
};

export type ZammadUser = {
  id: number;
  email: string | null;
  firstName: string | null;
  lastName: string | null;
  login: string | null;
  organizationId: number | null;
  active: boolean;
};

export type ZammadState = {
  id: number;
  name: string;
  stateTypeId: number | null;
  active: boolean;
};

export type ZammadTicketSearchItem = {
  id: number;
  number: number | null;
  title: string;
  customerId: number | null;
  customerEmail: string | null;
  customerName: string | null;
  groupId: number | null;
  groupName: string | null;
  stateId: number | null;
  stateName: string | null;
  articleCount: number | null;
  createdAt: string | null;
  updatedAt: string | null;
};

export type ZammadTicketSearchPage = {
  items: ZammadTicketSearchItem[];
  total: number | null;
  page: number;
  perPage: number;
};

export type ZammadTicketSearchParams = {
  q?: string;
  groupIds?: number[];
  stateIds?: number[];
  page?: number;
  perPage?: number;
};

export const zammadAdminApi = {
  status: () =>
    request<ZammadStatus>("GET", "/api/admin/integrations/zammad/status"),
  secretStatus: () =>
    request<ZammadSecretStatus>("GET", "/api/admin/integrations/zammad/secret"),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/integrations/zammad/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/integrations/zammad/secret"),
  setBaseUrl: (baseUrl: string) =>
    request<{ baseUrl: string }>(
      "PUT",
      "/api/admin/integrations/zammad/base-url",
      { baseUrl },
    ),
  testConnection: () =>
    request<ZammadTestResult>(
      "POST",
      "/api/admin/integrations/zammad/test-connection",
    ),
  listGroups: () =>
    request<{ items: ZammadGroup[] }>(
      "GET",
      "/api/admin/integrations/zammad/groups",
    ),
  listStates: () =>
    request<{ items: ZammadState[] }>(
      "GET",
      "/api/admin/integrations/zammad/states",
    ),
  getUser: (userId: number) =>
    request<ZammadUser>(
      "GET",
      `/api/admin/integrations/zammad/users/${userId}`,
    ),
  searchTickets: (params: ZammadTicketSearchParams) => {
    const qs = new URLSearchParams();
    if (params.q && params.q.trim().length > 0) qs.set("q", params.q.trim());
    if (params.groupIds && params.groupIds.length > 0) {
      for (const id of params.groupIds) qs.append("groupIds", String(id));
    }
    if (params.stateIds && params.stateIds.length > 0) {
      for (const id of params.stateIds) qs.append("stateIds", String(id));
    }
    if (params.page) qs.set("page", String(params.page));
    if (params.perPage) qs.set("perPage", String(params.perPage));
    const suffix = qs.toString();
    return request<ZammadTicketSearchPage>(
      "GET",
      suffix.length > 0
        ? `/api/admin/integrations/zammad/tickets/search?${suffix}`
        : "/api/admin/integrations/zammad/tickets/search",
    );
  },
  auditLog: (cursor: number | null, limit = 50) => {
    const qs = new URLSearchParams();
    if (cursor !== null) qs.set("cursor", String(cursor));
    qs.set("limit", String(limit));
    return request<IntegrationAuditPage>(
      "GET",
      `/api/admin/integrations/zammad/audit?${qs.toString()}`,
    );
  },
};

// ---- Zammad mapping (v0.0.41 phase 3) ----

export type ZammadMappingRow = {
  zammadId: number;
  zammadName: string;
  zammadActive: boolean;
  mappedToId: string | null;
  mappedToName: string | null;
};

export type ZammadMappingTarget = {
  id: string;
  name: string;
  isActive: boolean;
};

export type ZammadMappingOverview = {
  groups: ZammadMappingRow[];
  states: ZammadMappingRow[];
  priorities: ZammadMappingRow[];
  localQueues: ZammadMappingTarget[];
  localStatuses: ZammadMappingTarget[];
  localPriorities: ZammadMappingTarget[];
  unmappedGroupCount: number;
  unmappedStateCount: number;
  unmappedPriorityCount: number;
};

export const zammadMappingApi = {
  overview: () =>
    request<ZammadMappingOverview>(
      "GET",
      "/api/admin/integrations/zammad/mappings/overview",
    ),
  putGroup: (zammadGroupId: number, zammadName: string, targetId: string) =>
    request<void>(
      "PUT",
      `/api/admin/integrations/zammad/mappings/groups/${zammadGroupId}`,
      { zammadName, targetId },
    ),
  deleteGroup: (zammadGroupId: number) =>
    request<void>(
      "DELETE",
      `/api/admin/integrations/zammad/mappings/groups/${zammadGroupId}`,
    ),
  putState: (zammadStateId: number, zammadName: string, targetId: string) =>
    request<void>(
      "PUT",
      `/api/admin/integrations/zammad/mappings/states/${zammadStateId}`,
      { zammadName, targetId },
    ),
  deleteState: (zammadStateId: number) =>
    request<void>(
      "DELETE",
      `/api/admin/integrations/zammad/mappings/states/${zammadStateId}`,
    ),
  putPriority: (
    zammadPriorityId: number,
    zammadName: string,
    targetId: string,
  ) =>
    request<void>(
      "PUT",
      `/api/admin/integrations/zammad/mappings/priorities/${zammadPriorityId}`,
      { zammadName, targetId },
    ),
  deletePriority: (zammadPriorityId: number) =>
    request<void>(
      "DELETE",
      `/api/admin/integrations/zammad/mappings/priorities/${zammadPriorityId}`,
    ),
};

// ---- Zammad dry-run (v0.0.41 phase 3) ----

export type ZammadImportRunKind = "DryRun" | "Import";
export type ZammadImportRunStatus =
  | "Pending"
  | "Running"
  | "Completed"
  | "Failed"
  | "Cancelled";

export type ZammadImportTotals = {
  processed: number;
  mapped: number;
  skippedNoContact: number;
  skippedNoGroupMapping: number;
  skippedNoStateMapping: number;
  skippedNoPriorityMapping: number;
  failed: number;
  plannedTotal: number | null;
  imported: number;
  alreadyImported: number;
};

export type ZammadImportRunSummary = {
  id: string;
  kind: ZammadImportRunKind;
  status: ZammadImportRunStatus;
  startedByUserId: string | null;
  startedByDisplayName: string | null;
  startedUtc: string;
  finishedUtc: string | null;
  totals: ZammadImportTotals;
  errorMessage: string | null;
};

export type ZammadImportSourceFilter = {
  ticketIds: number[] | null;
  freeText: string | null;
  groupIds: number[] | null;
  stateIds: number[] | null;
  selectAllMatching: boolean;
  dryRunId: string | null;
};

export type ZammadImportRunDetail = {
  summary: ZammadImportRunSummary;
  sourceFilter: ZammadImportSourceFilter | null;
};

export type ZammadImportRecordResult =
  | "mapped"
  | "skipped_no_contact"
  | "skipped_no_group_mapping"
  | "skipped_no_state_mapping"
  | "skipped_no_priority_mapping"
  | "failed"
  | "imported"
  | "already_imported";

export type ZammadImportRecordItem = {
  id: string;
  zammadTicketId: number;
  zammadTicketNumber: string | null;
  zammadTicketTitle: string | null;
  result: ZammadImportRecordResult;
  unresolvedReasons: string[];
  mappingJson: string;
  wouldCreateTicketId: string | null;
  createdUtc: string;
};

export type ZammadImportRecordPage = {
  items: ZammadImportRecordItem[];
  nextCursor: string | null;
};

export type ZammadDryRunStartRequest = {
  ticketIds?: number[];
  freeText?: string;
  groupIds?: number[];
  stateIds?: number[];
  selectAllMatching: boolean;
};

export const zammadDryRunApi = {
  start: (req: ZammadDryRunStartRequest) =>
    request<{ runId: string }>(
      "POST",
      "/api/admin/integrations/zammad/import/dry-run",
      req,
    ),
  listRuns: (limit = 50) =>
    request<{ items: ZammadImportRunSummary[] }>(
      "GET",
      `/api/admin/integrations/zammad/import/runs?limit=${limit}`,
    ),
  getRun: (runId: string) =>
    request<ZammadImportRunDetail>(
      "GET",
      `/api/admin/integrations/zammad/import/runs/${runId}`,
    ),
  getRecords: (
    runId: string,
    options: { cursor?: string | null; limit?: number; result?: string } = {},
  ) => {
    const qs = new URLSearchParams();
    if (options.cursor) qs.set("cursor", options.cursor);
    if (options.limit) qs.set("limit", String(options.limit));
    if (options.result) qs.set("result", options.result);
    const suffix = qs.toString();
    return request<ZammadImportRecordPage>(
      "GET",
      suffix.length > 0
        ? `/api/admin/integrations/zammad/import/runs/${runId}/records?${suffix}`
        : `/api/admin/integrations/zammad/import/runs/${runId}/records`,
    );
  },
  cancel: (runId: string) =>
    request<void>(
      "POST",
      `/api/admin/integrations/zammad/import/runs/${runId}/cancel`,
    ),
  recheck: (runId: string, recordIds: string[]) =>
    request<{ rechecked: number }>(
      "POST",
      `/api/admin/integrations/zammad/import/runs/${runId}/recheck`,
      { recordIds },
    ),
  startImport: (dryRunId: string) =>
    request<{ runId: string }>(
      "POST",
      "/api/admin/integrations/zammad/import/import",
      { dryRunId },
    ),
};

// ---- Zammad KB-import (v0.0.43) ------------------------------------

export type ZammadKnowledgeBase = {
  id: number;
  name: string;
  active: boolean;
  defaultLocale?: string | null;
  categoryCount: number;
  answerCount: number;
};

export type ZammadKbSectionAction = "create" | "merge" | "skip";

export type ZammadKbProposalNode = {
  zammadCategoryId: number;
  zammadParentId?: number | null;
  depth: number;
  position: number;
  proposedTitle: string;
  proposedSlug: string;
  action: ZammadKbSectionAction;
  targetSectionId?: string | null;
  answerCount: number;
};

export type ZammadKbProposal = {
  knowledgeBaseId: number;
  knowledgeBaseName: string;
  defaultLocale: string;
  nodes: ZammadKbProposalNode[];
  totalAnswerCount: number;
};

export type ZammadKbImportRunStatus =
  | "Pending"
  | "Proposing"
  | "AwaitingApproval"
  | "Approved"
  | "Importing"
  | "Completed"
  | "Failed"
  | "Cancelled";

export type ZammadKbImportTotals = {
  plannedTotal: number | null;
  processed: number;
  imported: number;
  alreadyImported: number;
  skippedNoSectionMapping: number;
  skippedNoTranslation: number;
  skippedSectionSkipped: number;
  failed: number;
};

export type ZammadKbImportRunSummary = {
  id: string;
  status: ZammadKbImportRunStatus;
  startedByUserId?: string | null;
  startedByDisplayName?: string | null;
  startedUtc: string;
  finishedUtc?: string | null;
  sourceKbId?: number | null;
  sourceKbName?: string | null;
  totals: ZammadKbImportTotals;
  errorMessage?: string | null;
};

export type ZammadKbPickerItem = {
  zammadAnswerId: number;
  zammadCategoryId: number;
  categoryTitle?: string | null;
  title: string;
  status: string;
  promoted: boolean;
  hasTranslation: boolean;
  updatedAt?: string | null;
};

export type ZammadKbPickerPage = {
  items: ZammadKbPickerItem[];
  total: number;
};

export type ZammadKbImportRecordItem = {
  id: string;
  zammadAnswerId: number;
  zammadCategoryId?: number | null;
  zammadTitle?: string | null;
  result: string;
  unresolvedReasons: string[];
  mappingJson: string;
  targetArticleId?: string | null;
  createdUtc: string;
};

export type ZammadKbImportRecordPage = {
  items: ZammadKbImportRecordItem[];
  nextCursor: string | null;
};

export const zammadKbImportApi = {
  listKnowledgeBases: () =>
    request<{ items: ZammadKnowledgeBase[] }>(
      "GET",
      "/api/admin/integrations/zammad/kb-import/knowledge-bases",
    ),
  startRun: () =>
    request<{ runId: string }>(
      "POST",
      "/api/admin/integrations/zammad/kb-import/runs",
    ),
  listRuns: (limit = 25) =>
    request<{ items: ZammadKbImportRunSummary[] }>(
      "GET",
      `/api/admin/integrations/zammad/kb-import/runs?limit=${limit}`,
    ),
  getRun: (runId: string) =>
    request<{ summary: ZammadKbImportRunSummary }>(
      "GET",
      `/api/admin/integrations/zammad/kb-import/runs/${runId}`,
    ),
  buildProposal: (runId: string, knowledgeBaseId: number) =>
    request<ZammadKbProposal>(
      "POST",
      `/api/admin/integrations/zammad/kb-import/runs/${runId}/proposal`,
      { knowledgeBaseId },
    ),
  getProposal: (runId: string) =>
    request<ZammadKbProposal>(
      "GET",
      `/api/admin/integrations/zammad/kb-import/runs/${runId}/proposal`,
    ),
  saveDecisions: (runId: string, nodes: ZammadKbProposalNode[]) =>
    request<void>(
      "POST",
      `/api/admin/integrations/zammad/kb-import/runs/${runId}/proposal/decisions`,
      { nodes },
    ),
  applyProposal: (runId: string) =>
    request<{ mappingCount: number }>(
      "POST",
      `/api/admin/integrations/zammad/kb-import/runs/${runId}/proposal/apply`,
    ),
  picker: (
    runId: string,
    options: {
      status?: string;
      categoryId?: number;
      freeText?: string;
      page?: number;
      pageSize?: number;
    } = {},
  ) => {
    const qs = new URLSearchParams();
    if (options.status) qs.set("status", options.status);
    if (options.categoryId !== undefined) qs.set("categoryId", String(options.categoryId));
    if (options.freeText) qs.set("freeText", options.freeText);
    if (options.page) qs.set("page", String(options.page));
    if (options.pageSize) qs.set("pageSize", String(options.pageSize));
    const suffix = qs.toString();
    return request<ZammadKbPickerPage>(
      "GET",
      suffix.length > 0
        ? `/api/admin/integrations/zammad/kb-import/runs/${runId}/picker?${suffix}`
        : `/api/admin/integrations/zammad/kb-import/runs/${runId}/picker`,
    );
  },
  startImport: (runId: string, answerIds: number[]) =>
    request<void>(
      "POST",
      `/api/admin/integrations/zammad/kb-import/runs/${runId}/import`,
      { answerIds },
    ),
  records: (
    runId: string,
    options: { cursor?: string | null; limit?: number; result?: string } = {},
  ) => {
    const qs = new URLSearchParams();
    if (options.cursor) qs.set("cursor", options.cursor);
    if (options.limit) qs.set("limit", String(options.limit));
    if (options.result) qs.set("result", options.result);
    const suffix = qs.toString();
    return request<ZammadKbImportRecordPage>(
      "GET",
      suffix.length > 0
        ? `/api/admin/integrations/zammad/kb-import/runs/${runId}/records?${suffix}`
        : `/api/admin/integrations/zammad/kb-import/runs/${runId}/records`,
    );
  },
  cancel: (runId: string) =>
    request<void>(
      "POST",
      `/api/admin/integrations/zammad/kb-import/runs/${runId}/cancel`,
    ),
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

export type TriggerActivatorKind = "action" | "time" | "manual";
export type TriggerActivatorMode =
  | "selective"
  | "always"
  | "reminder"
  | "escalation"
  | "escalation_warning"
  // v0.0.39 — sole mode for manual triggers today. The trigger runs
  // only when an agent clicks "Create linked X ticket" in the side
  // panel; never fired by the event evaluator or scheduler.
  | "linked_ticket_creator";

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
  groupId: string | null;
  sortOrder: number;
  runs: TriggerRunSummary;
};

export type TriggerGroup = {
  id: string;
  name: string;
  color: string | null;
  sortOrder: number;
  createdUtc: string;
  updatedUtc: string;
};

export type TriggerGroupInput = {
  name: string;
  color: string | null;
};

export type TriggerPlacement = {
  id: string;
  groupId: string | null;
  sortOrder: number;
};

export type TriggerGroupPlacement = {
  id: string;
  sortOrder: number;
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
  // v0.0.39 — only populated for manual triggers; identifies the
  // ticket-type the trigger is bound to.
  manualTicketTypeId?: string | null;
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
  // v0.0.39 — required when activatorKind === "manual"; ignored
  // otherwise. The server validates the pairing.
  manualTicketTypeId?: string | null;
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
  reorder: (items: TriggerPlacement[]) =>
    request<void>("POST", "/api/admin/triggers/reorder", { items }),
};

export const triggerGroupApi = {
  list: () =>
    request<{ items: TriggerGroup[] }>("GET", "/api/admin/trigger-groups"),
  create: (body: TriggerGroupInput) =>
    request<TriggerGroup>("POST", "/api/admin/trigger-groups", body),
  update: (id: string, body: TriggerGroupInput) =>
    request<TriggerGroup>("PUT", `/api/admin/trigger-groups/${id}`, body),
  remove: (id: string) =>
    request<void>("DELETE", `/api/admin/trigger-groups/${id}`),
  reorder: (items: TriggerGroupPlacement[]) =>
    request<void>("POST", "/api/admin/trigger-groups/reorder", { items }),
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
