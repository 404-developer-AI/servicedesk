import { request } from "@/lib/http";
import type { IntegrationAuditPage } from "@/lib/adsolut-api";
import type { IntegrationLinkableCompany } from "@/lib/sophos-api";

// ---- Veeam backup matching (Veeam Service Provider Console) ----

export type VeeamConnectionState = "disabled" | "not-configured" | "ready";
export type VeeamStatus = {
  state: VeeamConnectionState;
  enabled: boolean;
  baseUrl: string | null;
  username: string | null;
  passwordConfigured: boolean;
  syncIntervalMinutes: number;
  allowSelfSignedTls: boolean;
  lastCheckedUtc: string | null;
  lastChangedUtc: string | null;
  lastStatus: string | null;
  lastError: string | null;
  companyCount: number;
  objectCount: number;
};
export type VeeamSecretStatus = { configured: boolean };
export type VeeamTestResult = {
  success: boolean;
  error?: string;
  message?: string;
  latencyMs: number;
  companyCount?: number;
};
export type VeeamSyncResult = {
  success: boolean;
  status: string;
  error: string | null;
  companyCount: number;
  objectCount: number;
  changed: boolean;
};
export type VeeamCompany = {
  companyId: string;
  companyCode: string | null;
  companyName: string | null;
  vspcCompanyName: string | null;
  objectCount: number;
  lastSyncedUtc: string | null;
};
export type VeeamVspcCompany = {
  vspcCompanyUid: string;
  code: string | null;
  name: string | null;
  matchedCompanyId: string | null;
  matchedCompanyName: string | null;
};
export type VeeamCompanyLink = {
  parentCompanyId: string;
  parentCompanyName: string | null;
  vspcCompanyUid: string;
  vspcCompanyName: string | null;
  vspcCompanyCode: string | null;
};
export const veeamAdminApi = {
  status: () =>
    request<VeeamStatus>("GET", "/api/admin/integrations/veeam/status"),
  secretStatus: () =>
    request<VeeamSecretStatus>("GET", "/api/admin/integrations/veeam/secret"),
  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/integrations/veeam/secret", { value }),
  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/integrations/veeam/secret"),
  setConfig: (baseUrl: string, username: string, allowSelfSignedTls: boolean) =>
    request<{ baseUrl: string; username: string; allowSelfSignedTls: boolean }>(
      "PUT",
      "/api/admin/integrations/veeam/config",
      { baseUrl, username, allowSelfSignedTls },
    ),
  setEnabled: (enabled: boolean) =>
    request<{ enabled: boolean }>(
      "PUT",
      "/api/admin/integrations/veeam/enabled",
      { enabled },
    ),
  setSyncInterval: (minutes: number) =>
    request<{ syncIntervalMinutes: number }>(
      "PUT",
      "/api/admin/integrations/veeam/sync-interval",
      { minutes },
    ),
  testConnection: () =>
    request<VeeamTestResult>(
      "POST",
      "/api/admin/integrations/veeam/test-connection",
    ),
  sync: () =>
    request<VeeamSyncResult>("POST", "/api/admin/integrations/veeam/sync"),
  companies: () =>
    request<{ items: VeeamCompany[] }>(
      "GET",
      "/api/admin/integrations/veeam/companies",
    ),
  vspcCompanies: () =>
    request<{ items: VeeamVspcCompany[] }>(
      "GET",
      "/api/admin/integrations/veeam/vspc-companies",
    ),
  links: () =>
    request<{ items: VeeamCompanyLink[] }>(
      "GET",
      "/api/admin/integrations/veeam/links",
    ),
  linkableCompanies: () =>
    request<{ items: IntegrationLinkableCompany[] }>(
      "GET",
      "/api/admin/integrations/veeam/linkable-companies",
    ),
  addLink: (parentCompanyId: string, vspcCompanyUid: string) =>
    request<void>("POST", "/api/admin/integrations/veeam/links", {
      parentCompanyId,
      vspcCompanyUid,
    }),
  removeLink: (parentCompanyId: string, vspcCompanyUid: string) =>
    request<void>(
      "DELETE",
      `/api/admin/integrations/veeam/links/${parentCompanyId}/${encodeURIComponent(vspcCompanyUid)}`,
    ),
  auditLog: (cursor: number | null, limit = 50) => {
    const qs = new URLSearchParams();
    if (cursor !== null) qs.set("cursor", String(cursor));
    qs.set("limit", String(limit));
    return request<IntegrationAuditPage>(
      "GET",
      `/api/admin/integrations/veeam/audit?${qs.toString()}`,
    );
  },
};
