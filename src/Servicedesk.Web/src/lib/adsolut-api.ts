import { request } from "@/lib/http";
import type {
  AdsolutErpArticlesState,
  AdsolutErpContractsState,
  AdsolutErpOrdersState,
  AdsolutErpSalesReceiptsState,
} from "@/lib/adsolut-erp-api";

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
export type AdsolutErpProbeAttempt = {
  label: string;
  status: number;
  requestUrl: string;
  upstreamErrorCode: string | null;
  body: string;
};
export type AdsolutErpProbeResponse = {
  attempts: AdsolutErpProbeAttempt[];
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
  /// ERP SalesReceipts ("verkoopbonnen") preview — fans out a fixed set of
  /// read-only GETs against the ERP SalesReceiptInfos endpoint so an admin
  /// can inspect the real response shape (and localise a WK-side failure)
  /// before the mirror/sync slice is built. Each attempt reuses the
  /// debug-lookup envelope (status + requestUrl + upstreamErrorCode + body).
  debugErpSalesReceipts: () =>
    request<AdsolutErpProbeResponse>(
      "GET",
      "/api/admin/integrations/adsolut/debug/erp-sales-receipts",
    ),
  /// ERP SalesReceipts (verkoopbonnen) mirror — integration-page controls.
  erpSalesReceiptsState: () =>
    request<AdsolutErpSalesReceiptsState>(
      "GET",
      "/api/admin/integrations/adsolut/erp/sales-receipts/state",
    ),
  triggerErpSalesReceiptsSync: () =>
    request<void>("POST", "/api/admin/integrations/adsolut/erp/sales-receipts/sync"),
  setErpSalesReceiptsStatusFilter: (codes: string[]) =>
    request<{ statusFilter: string[] }>(
      "PUT",
      "/api/admin/integrations/adsolut/erp/sales-receipts/status-filter",
      { codes },
    ),
  /// ERP Orders (bestellingen) mirror — integration-page controls (v0.0.59).
  erpOrdersState: () =>
    request<AdsolutErpOrdersState>(
      "GET",
      "/api/admin/integrations/adsolut/erp/orders/state",
    ),
  triggerErpOrdersSync: () =>
    request<void>("POST", "/api/admin/integrations/adsolut/erp/orders/sync"),
  setErpOrdersStatusFilter: (codes: string[]) =>
    request<{ statusFilter: string[] }>(
      "PUT",
      "/api/admin/integrations/adsolut/erp/orders/status-filter",
      { codes },
    ),
  setErpOrdersSupplierStatusColors: (colors: Record<string, string>) =>
    request<{ statusColors: Record<string, string> }>(
      "PUT",
      "/api/admin/integrations/adsolut/erp/orders/supplier-status-colors",
      { colors },
    ),
  /// ERP Articles (artikels) mirror — integration-page controls (v0.0.76).
  erpArticlesState: () =>
    request<AdsolutErpArticlesState>(
      "GET",
      "/api/admin/integrations/adsolut/erp/articles/state",
    ),
  triggerErpArticlesSync: () =>
    request<void>("POST", "/api/admin/integrations/adsolut/erp/articles/sync"),
  /// ERP Contracts (contracten) mirror — integration-page controls.
  erpContractsState: () =>
    request<AdsolutErpContractsState>(
      "GET",
      "/api/admin/integrations/adsolut/erp/contracts/state",
    ),
  triggerErpContractsSync: () =>
    request<void>("POST", "/api/admin/integrations/adsolut/erp/contracts/sync"),
  setErpContractsStatusFilter: (codes: string[]) =>
    request<{ statusFilter: string[] }>(
      "PUT",
      "/api/admin/integrations/adsolut/erp/contracts/status-filter",
      { codes },
    ),
  setErpContractsStatusColors: (colors: Record<string, string>) =>
    request<{ statusColors: Record<string, string> }>(
      "PUT",
      "/api/admin/integrations/adsolut/erp/contracts/status-colors",
      { colors },
    ),
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
