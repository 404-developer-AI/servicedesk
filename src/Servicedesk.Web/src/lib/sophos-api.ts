import { request } from "@/lib/http";
import type { IntegrationAuditPage } from "@/lib/adsolut-api";

// ---- Sophos Central spam-filter matching (v0.0.78) ----

export type SophosConnectionState = "disabled" | "not-configured" | "ready";
export type SophosStatus = {
  state: SophosConnectionState;
  enabled: boolean;
  clientIdConfigured: boolean;
  clientSecretConfigured: boolean;
  syncIntervalMinutes: number;
  lastCheckedUtc: string | null;
  lastChangedUtc: string | null;
  lastStatus: string | null;
  lastError: string | null;
  tenantCount: number;
  mailboxCount: number;
};
export type SophosTestResult = {
  success: boolean;
  error?: string;
  message?: string;
  idType?: string | null;
  tenantCount?: number;
};
export type SophosSyncResult = {
  success: boolean;
  status: string;
  error: string | null;
  tenantCount: number;
  mailboxCount: number;
  changed: boolean;
};
/** A Microsoft 365-connected company an extra tenant can be rolled into. */
export type IntegrationLinkableCompany = {
  companyId: string;
  name: string | null;
  code: string | null;
};
export type SophosTenantLink = {
  companyId: string;
  companyName: string | null;
};
export type SophosTenant = {
  tenantId: string;
  name: string | null;
  showAs: string | null;
  companyCode: string | null;
  companyId: string | null;
  companyName: string | null;
  status: string | null;
  m365Connected: boolean;
  mailboxCount: number;
  lastSyncedUtc: string | null;
  links: SophosTenantLink[];
};
export const sophosAdminApi = {
  status: () =>
    request<SophosStatus>("GET", "/api/admin/integrations/sophos/status"),
  setCredentials: (clientId: string | null, clientSecret: string | null) =>
    request<void>("PUT", "/api/admin/integrations/sophos/credentials", {
      clientId,
      clientSecret,
    }),
  deleteCredentials: () =>
    request<void>("DELETE", "/api/admin/integrations/sophos/credentials"),
  setEnabled: (enabled: boolean) =>
    request<{ enabled: boolean }>("PUT", "/api/admin/integrations/sophos/enabled", {
      enabled,
    }),
  setSyncInterval: (minutes: number) =>
    request<{ syncIntervalMinutes: number }>(
      "PUT",
      "/api/admin/integrations/sophos/sync-interval",
      { minutes },
    ),
  testConnection: () =>
    request<SophosTestResult>("POST", "/api/admin/integrations/sophos/test-connection"),
  sync: () =>
    request<SophosSyncResult>("POST", "/api/admin/integrations/sophos/sync"),
  tenants: () =>
    request<{ items: SophosTenant[] }>("GET", "/api/admin/integrations/sophos/tenants"),
  linkableCompanies: () =>
    request<{ items: IntegrationLinkableCompany[] }>(
      "GET",
      "/api/admin/integrations/sophos/linkable-companies",
    ),
  addTenantLink: (tenantId: string, companyId: string) =>
    request<void>(
      "POST",
      `/api/admin/integrations/sophos/tenants/${encodeURIComponent(tenantId)}/links`,
      { companyId },
    ),
  removeTenantLink: (tenantId: string, companyId: string) =>
    request<void>(
      "DELETE",
      `/api/admin/integrations/sophos/tenants/${encodeURIComponent(tenantId)}/links/${companyId}`,
    ),
  auditLog: (cursor: number | null, limit = 50) => {
    const qs = new URLSearchParams();
    if (cursor !== null) qs.set("cursor", String(cursor));
    qs.set("limit", String(limit));
    return request<IntegrationAuditPage>(
      "GET",
      `/api/admin/integrations/sophos/audit?${qs.toString()}`,
    );
  },
};
