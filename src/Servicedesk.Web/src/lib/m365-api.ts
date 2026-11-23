import { request } from "@/lib/http";
import type { IntegrationAuditPage } from "@/lib/adsolut-api";

// ---- Microsoft 365 customer-tenant reader (v0.0.77) ----

export type M365ConnectionState = "disabled" | "not-configured" | "ready";
export type M365RequiredPermission = {
  name: string;
  type: string;
  description: string;
};
export type M365Status = {
  state: M365ConnectionState;
  enabled: boolean;
  tenantId: string | null;
  clientId: string | null;
  clientSecretConfigured: boolean;
  redirectUri: string;
  requiredPermissions: M365RequiredPermission[];
  syncIntervalMinutes: number;
};
export type M365SecretStatus = { configured: boolean };
export type M365TestResult = {
  success: boolean;
  error?: string;
  message?: string;
  latencyMs: number;
};
export const m365AdminApi = {
  status: () =>
    request<M365Status>("GET", "/api/admin/integrations/m365/status"),
  secretStatus: () =>
    request<M365SecretStatus>("GET", "/api/admin/integrations/m365/secret"),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/integrations/m365/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/integrations/m365/secret"),
  setConfig: (tenantId: string, clientId: string) =>
    request<{ tenantId: string; clientId: string }>(
      "PUT",
      "/api/admin/integrations/m365/config",
      { tenantId, clientId },
    ),
  setEnabled: (enabled: boolean) =>
    request<{ enabled: boolean }>(
      "PUT",
      "/api/admin/integrations/m365/enabled",
      { enabled },
    ),
  setSyncInterval: (minutes: number) =>
    request<{ syncIntervalMinutes: number }>(
      "PUT",
      "/api/admin/integrations/m365/sync-interval",
      { minutes },
    ),
  testConnection: () =>
    request<M365TestResult>(
      "POST",
      "/api/admin/integrations/m365/test-connection",
    ),
  auditLog: (cursor: number | null, limit = 50) => {
    const qs = new URLSearchParams();
    if (cursor !== null) qs.set("cursor", String(cursor));
    qs.set("limit", String(limit));
    return request<IntegrationAuditPage>(
      "GET",
      `/api/admin/integrations/m365/audit?${qs.toString()}`,
    );
  },
};
