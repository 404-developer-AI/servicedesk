import { request } from "@/lib/http";

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
