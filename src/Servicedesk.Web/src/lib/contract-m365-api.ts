import { request } from "@/lib/http";

// ---- Contracts → Microsoft 365 matching -----------------------------
// Curated "M365-related" article selection (the gear) + the matched-companies
// list. A company surfaces when any of its contract lines reference a selected
// article (each company once). Same contracts_enabled gate as the rest.

export type M365SelectedArticle = { id: string; code: string | null; name: string | null };
export type M365Company = { companyId: string; code: string | null; name: string | null };
export type M365ContractStatusOption = {
  code: string;
  description: string | null;
  count: number;
};
export type M365Selection = {
  articles: M365SelectedArticle[];
  statusCodes: string[];
  statusOptions: M365ContractStatusOption[];
};
/// Per-company connection lifecycle. "needs_reconsent" means a read failed with
/// 401/403 (consent revoked or a new permission added); "error" is transient.
export type M365LinkStatus =
  | "connected"
  | "needs_reconsent"
  | "error"
  | "disconnected";
export type M365Connection = {
  companyId: string;
  status: M365LinkStatus;
  consentedAt: string | null;
  lastCheckedUtc: string | null;
  lastChangedUtc: string | null;
  mailboxCount: number;
  lastError: string | null;
};
export type M365Mailbox = {
  objectId: string;
  mailboxType: string | null;
  displayName: string | null;
  givenName: string | null;
  surname: string | null;
  upn: string | null;
  mail: string | null;
  enabled: boolean | null;
  licenses: string | null;
  // Sophos spam-filter verdict. null when the Sophos overlay is unavailable
  // (integration off or no matched tenant); otherwise Protected (true) /
  // Unprotected (false).
  spamFilterProtected: boolean | null;
  // Veeam backup status per service. null when Veeam is off or the company is
  // not known in VSPC; the restore-point count + last-backup date feed the
  // Protected pill's hover.
  exchangeProtected: boolean | null;
  exchangeRestorePoints: number | null;
  exchangeLastBackupUtc: string | null;
  onedriveProtected: boolean | null;
  onedriveRestorePoints: number | null;
  onedriveLastBackupUtc: string | null;
};
export type M365CompanyMailboxes = {
  companyId: string;
  companyCode: string | null;
  companyName: string | null;
  status: M365LinkStatus | null;
  tenantId: string | null;
  consentedAt: string | null;
  lastCheckedUtc: string | null;
  lastChangedUtc: string | null;
  lastError: string | null;
  // When true, the Sophos spam-filter column is shown and each mailbox carries
  // a Protected/Unprotected verdict.
  spamFilterAvailable: boolean;
  // When true, the OneDrive + Exchange backup columns are shown (the company is
  // known in VSPC).
  veeamAvailable: boolean;
  mailboxes: M365Mailbox[];
};
export type M365SyncResult = {
  success: boolean;
  status: string;
  error: string | null;
  mailboxCount: number;
  changed: boolean;
};
export const contractM365Api = {
  getSelection: () =>
    request<M365Selection>("GET", "/api/contracts/m365/selection"),
  saveSelection: (articleIds: string[], statusCodes: string[]) =>
    request<{ ok: boolean; count: number; statusCount: number }>(
      "PUT",
      "/api/contracts/m365/selection",
      { articleIds, statusCodes },
    ),
  companies: () => request<{ items: M365Company[] }>("GET", "/api/contracts/m365/companies"),
  connections: () =>
    request<{ items: M365Connection[] }>("GET", "/api/contracts/m365/companies/connections"),
  connect: (companyId: string) =>
    request<{ consentUrl: string }>(
      "POST",
      `/api/contracts/m365/companies/${companyId}/connect`,
    ),
  sync: (companyId: string) =>
    request<M365SyncResult>("POST", `/api/contracts/m365/companies/${companyId}/sync`),
  disconnect: (companyId: string) =>
    request<{ ok: boolean }>(
      "POST",
      `/api/contracts/m365/companies/${companyId}/disconnect`,
    ),
  mailboxes: (companyId: string) =>
    request<M365CompanyMailboxes>(
      "GET",
      `/api/contracts/m365/companies/${companyId}/mailboxes`,
    ),
};
