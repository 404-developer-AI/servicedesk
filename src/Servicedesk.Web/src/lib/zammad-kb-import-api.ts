import { request } from "@/lib/http";

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
