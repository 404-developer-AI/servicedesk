import { request } from "@/lib/http";
import type { IntegrationAuditPage } from "@/lib/adsolut-api";

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
