import { request } from "@/lib/http";
import type { IntegrationAuditPage } from "@/lib/adsolut-api";

// ---- Tactical RMM admin (v0.0.52) ----

export type TrmmConnectionState = "disabled" | "not-configured" | "ready";
export type TrmmStatus = {
  state: TrmmConnectionState;
  enabled: boolean;
  apiKeyConfigured: boolean;
  baseUrl: string | null;
  syncIntervalMinutes: number;
  lastSyncUtc: string | null;
  lastStatus: "ok" | "failed" | null;
  lastError: string | null;
  // v0.1.10 — Remote Desktop check sync.
  rdsCheckEnabled: boolean;
  rdsCheckScriptName: string;
  rdsSyncIntervalMinutes: number;
  rdsIncludeWorkstations: boolean;
  lastRdsSyncUtc: string | null;
  lastRdsStatus: "ok" | "failed" | null;
  lastRdsError: string | null;
};
export type TrmmRdsSettings = {
  enabled: boolean;
  scriptName: string;
  intervalMinutes: number;
  includeWorkstations: boolean;
};
export type TrmmRdsSyncResult = {
  success: boolean;
  agents: number;
  rds: number;
  notRds: number;
  failed: number;
  noCheck: number;
  pending: number;
  errors: number;
  latencyMs: number;
  errorCode: string | null;
  errorMessage: string | null;
};
export type TrmmSecretStatus = { configured: boolean };
export type TrmmTestResult = {
  success: boolean;
  clientCount?: number;
  latencyMs: number;
  errorCode?: string;
  message?: string;
};
export type TrmmSyncResult = {
  success: boolean;
  clients: number;
  sites: number;
  agents: number;
  autoLinkedCompanies: number;
  latencyMs: number;
  errorCode: string | null;
  errorMessage: string | null;
};
export type TrmmClientMapping = {
  trmmClientId: number;
  name: string;
  code: string | null;
  autoMatched: boolean;
  companyId: string | null;
  companyName: string | null;
  companyCode: string | null;
  agentCount: number;
};
export const trmmAdminApi = {
  status: () =>
    request<TrmmStatus>("GET", "/api/admin/integrations/trmm/status"),
  secretStatus: () =>
    request<TrmmSecretStatus>("GET", "/api/admin/integrations/trmm/secret"),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/integrations/trmm/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/integrations/trmm/secret"),
  setBaseUrl: (baseUrl: string) =>
    request<{ baseUrl: string }>(
      "PUT",
      "/api/admin/integrations/trmm/base-url",
      { baseUrl },
    ),
  setEnabled: (enabled: boolean) =>
    request<{ enabled: boolean }>(
      "PUT",
      "/api/admin/integrations/trmm/enabled",
      { enabled },
    ),
  setSyncInterval: (minutes: number) =>
    request<{ syncIntervalMinutes: number }>(
      "PUT",
      "/api/admin/integrations/trmm/sync-interval",
      { minutes },
    ),
  testConnection: () =>
    request<TrmmTestResult>(
      "POST",
      "/api/admin/integrations/trmm/test-connection",
    ),
  triggerSync: () =>
    request<TrmmSyncResult>("POST", "/api/admin/integrations/trmm/sync"),
  setRdsSettings: (body: TrmmRdsSettings) =>
    request<{
      rdsCheckEnabled: boolean;
      rdsCheckScriptName: string;
      rdsSyncIntervalMinutes: number;
      rdsIncludeWorkstations: boolean;
    }>("PUT", "/api/admin/integrations/trmm/rds-settings", body),
  triggerRdsSync: () =>
    request<TrmmRdsSyncResult>("POST", "/api/admin/integrations/trmm/rds-sync"),
  listClientMappings: () =>
    request<{ items: TrmmClientMapping[] }>(
      "GET",
      "/api/admin/integrations/trmm/client-mappings",
    ),
  setClientMapping: (
    trmmClientId: number,
    body: { companyId: string | null; clearOverride: boolean },
  ) =>
    request<void>(
      "PUT",
      `/api/admin/integrations/trmm/client-mappings/${trmmClientId}`,
      body,
    ),
  auditLog: (cursor: number | null, limit = 50) => {
    const qs = new URLSearchParams();
    if (cursor !== null) qs.set("cursor", String(cursor));
    qs.set("limit", String(limit));
    return request<IntegrationAuditPage>(
      "GET",
      `/api/admin/integrations/trmm/audit?${qs.toString()}`,
    );
  },
};
