import { request } from "@/lib/http";
import type { UiTheme } from "@/lib/theme";

export type SystemVersion = {
  version: string;
  commit: string;
  buildTime: string;
  updateRefreshMode: "auto" | "banner";
  updateCheckOnFocus: boolean;
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
  /// v0.0.99 — server-dictated client poll cadence (Health.PollIntervalSeconds).
  pollIntervalSeconds: number;
};
/// v0.0.99 — the public pill payload; `pollIntervalSeconds` is what every
/// health poller feeds into react-query's refetchInterval, see
/// `healthRefetchInterval` in `lib/healthPolling.ts`.
export type SystemHealth = {
  status: HealthStatus;
  pollIntervalSeconds: number;
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
  pollIntervalSeconds: number;
};
export const systemApi = {
  version: () => request<SystemVersion>("GET", "/api/system/version"),
  time: () => request<SystemTime>("GET", "/api/system/time"),
  health: () => request<SystemHealth>("GET", "/api/system/health"),
  maintenance: () => request<MaintenanceState>("GET", "/api/system/maintenance"),
  loginBanner: () => request<LoginBannerState>("GET", "/api/system/login-banner"),
  // v0.0.44 — anonymous read of the admin-wide default theme so the login
  // page can paint with the right palette on first visit (no localStorage yet).
  defaultTheme: () =>
    request<{ theme: UiTheme }>("GET", "/api/system/default-theme"),
  // v0.0.57 — admin-configurable human-facing ticket reference prefix, e.g.
  // "Ticket#". Drives the copy-to-clipboard value so a pasted reference
  // resolves back to the ticket in search / pickers / timesheet links.
  ticketReferencePrefix: () =>
    request<{ prefix: string }>("GET", "/api/system/ticket-reference-prefix"),
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
