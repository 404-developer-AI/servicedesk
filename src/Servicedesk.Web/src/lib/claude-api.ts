import { ApiError, apiErrorMessage } from "@/lib/api";

// Re-export so callers can catch ApiError without importing from api.ts directly.
export { ApiError, apiErrorMessage };

// ---- Types ----

export type ClaudeConnectionState =
  | "Disabled"
  | "NotConfigured"
  | "NeedsZeroDataRetention"
  | "Ready";

export type ClaudeStatus = {
  state: ClaudeConnectionState;
  enabled: boolean;
  apiKeyConfigured: boolean;
  zeroDataRetentionConfirmed: boolean;
  model: string;
};

export type ClaudeSecretStatus = {
  configured: boolean;
};

export type ClaudeUsageAgent = {
  userId: string;
  email: string;
  role: string;
  budgetOverrideCents: number | null;
  effectiveBudgetCents: number;
  monthSpendMicroEur: number;
  callCount: number;
};

export type ClaudeUsage = {
  monthStartUtc: string;
  defaultBudgetCents: number;
  agents: ClaudeUsageAgent[];
};

export type ClaudeSetAgentBudgetResult = {
  userId: string;
  budgetOverrideCents: number | null;
};

export type ClaudeTicketImage = {
  id: string;
  filename: string;
  mimeType: string;
  sizeBytes: number;
  url: string;
};

export type ClaudeTicketImagesResult = {
  items: ClaudeTicketImage[];
};

export type ClaudeProposalSuccess = {
  refused?: false;
  proposalText: string;
  proposalHtml: string;
  inputTokens: number;
  outputTokens: number;
  costMicroEur: number;
  imageCount: number;
  monthSpendMicroEur: number;
  monthBudgetMicroEur: number;
};

export type ClaudeProposalRefused = {
  refused: true;
  message: string;
  monthSpendMicroEur: number;
  monthBudgetMicroEur: number;
};

export type ClaudeProposalResult = ClaudeProposalSuccess | ClaudeProposalRefused;

// ---- Helpers (shared with api.ts patterns) ----

import { csrfHeader } from "@/lib/csrf";

async function request<T>(
  method: string,
  url: string,
  body?: unknown,
): Promise<T> {
  const isSafe = method === "GET" || method === "HEAD";
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(isSafe ? {} : csrfHeader()),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let parsed: unknown = null;
    try {
      const text = await res.text();
      if (text.length > 0) parsed = JSON.parse(text);
    } catch {
      // ignore — body wasn't JSON
    }
    throw new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`, parsed);
  }
  if (res.status === 204) {
    return undefined as T;
  }
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

// ---- Admin API ----

export const claudeAdminApi = {
  status: () =>
    request<ClaudeStatus>("GET", "/api/admin/integrations/claude-ai/status"),

  secretStatus: () =>
    request<ClaudeSecretStatus>("GET", "/api/admin/integrations/claude-ai/secret"),

  setSecret: (value: string) =>
    request<void>("PUT", "/api/admin/integrations/claude-ai/secret", { value }),

  deleteSecret: () =>
    request<void>("DELETE", "/api/admin/integrations/claude-ai/secret"),

  getUsage: () =>
    request<ClaudeUsage>("GET", "/api/admin/integrations/claude-ai/usage"),

  setAgentBudget: (userId: string, cents: number | null) =>
    request<ClaudeSetAgentBudgetResult>(
      "PUT",
      `/api/admin/integrations/claude-ai/agents/${encodeURIComponent(userId)}/budget`,
      { cents },
    ),
};

// ---- Agent/ticket API ----

export const claudeTicketApi = {
  getTicketImages: (ticketId: string) =>
    request<ClaudeTicketImagesResult>(
      "GET",
      `/api/tickets/${encodeURIComponent(ticketId)}/ai-proposal/images`,
    ),

  createTicketProposal: (ticketId: string, attachmentIds: string[]) =>
    request<ClaudeProposalResult>(
      "POST",
      `/api/tickets/${encodeURIComponent(ticketId)}/ai-proposal`,
      { attachmentIds },
    ),
};
