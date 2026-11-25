import { request } from "@/lib/http";

/// v0.1.67 — Campaigns: one mail per ticket to many tickets at once. Every
/// route is gated server-side by the per-user `campaigns_enabled` flag (403
/// when off, admins included). Recipient rows are filtered to the caller's
/// queues (`hiddenCount` says how many are not shown). Sending only queues
/// outbox rows; the server sends them one by one through the normal
/// single-ticket mail path and rechecks every rule right before each mail.

export type CampaignState = "Draft" | "Active" | "Archived";
export type CampaignDecision = "Approved" | "OtherTime" | "Declined";
export type CampaignRoundKind = "Initial" | "FollowUp";
export type CampaignMessageState = "Queued" | "Sending" | "Sent" | "Failed" | "Skipped" | "Cancelled";
export type CampaignRecipientState =
  | "NotSent"
  | "Queued"
  | "Failed"
  | "Skipped"
  | "ReplyReceived"
  | "TimerExpired"
  | "Waiting";

export interface CampaignListItem {
  id: string;
  name: string;
  state: CampaignState;
  projectTicketId: string | null;
  projectTicketNumber: number | null;
  createdByEmail: string | null;
  createdUtc: string;
  updatedUtc: string;
  ticketCount: number;
  sentCount: number;
  queuedCount: number;
  failedCount: number;
  lastSentUtc: string | null;
}

export interface CampaignRound {
  id: string;
  roundNo: number;
  kind: CampaignRoundKind;
  subject: string;
  sentByEmail: string | null;
  pendingStatusId: string | null;
  pendingTillUtc: string | null;
  scheduledUtc: string | null;
  startedUtc: string;
  completedUtc: string | null;
  total: number;
  sent: number;
  queued: number;
  failed: number;
  skipped: number;
  cancelled: number;
}

export interface CampaignRecipient {
  ticketId: string;
  number: number;
  subject: string;
  statusName: string;
  stateCategory: string;
  isProject: boolean;
  isMerged: boolean;
  pendingTillUtc: string | null;
  requesterName: string | null;
  requesterEmail: string | null;
  companyName: string | null;
  proposedUtc: string | null;
  source: "Selection" | "Project";
  addedUtc: string;
  decision: CampaignDecision | null;
  decisionUtc: string | null;
  decisionByEmail: string | null;
  /** Phase 2 — who recorded the decision: an agent or the customer (answer link). */
  decisionSource: "Agent" | "Customer" | null;
  /** The customer's optional note with an answer-link answer. */
  decisionNote: string | null;
  /** "Another moment" through the link: the customer's preferred date ("yyyy-MM-dd", server-local) + optional time. */
  decisionPreferredDate: string | null;
  decisionPreferredMinute: number | null;
  /** A scheduled round: when this ticket's mail is due. */
  notBeforeUtc: string | null;
  answerLinkExpiresUtc: string | null;
  messageState: CampaignMessageState | null;
  skipReason: string | null;
  lastError: string | null;
  pendingResult: string | null;
  roundNo: number | null;
  firstSentUtc: string | null;
  lastSentUtc: string | null;
  roundPendingTillUtc: string | null;
  replyUtc: string | null;
  replyType: string | null;
  state: CampaignRecipientState;
}

export interface CampaignDetail {
  id: string;
  name: string;
  subject: string;
  bodyHtml: string;
  /** Shown to the customer on the answer page with "Another moment" (plain text). */
  otherTimeNotice: string;
  sourceTemplateId: string | null;
  state: CampaignState;
  defaultProposedUtc: string | null;
  project: { id: string; number: number | null; subject: string | null; visible: boolean } | null;
  duplicatedFromId: string | null;
  /** Phase 3 — set for a maintenance planner campaign (one per type and month). */
  maintenance: { planId: string; planName: string | null; roundId: string | null } | null;
  createdByEmail: string | null;
  createdUtc: string;
  updatedUtc: string;
  rounds: CampaignRound[];
  recipients: CampaignRecipient[];
  hiddenCount: number;
}

export interface CampaignPlanItem {
  ticketId: string;
  number: number;
  ticketSubject: string;
  requesterName: string | null;
  requesterEmail: string | null;
  companyName: string | null;
  proposedUtc: string | null;
  skipReason: string | null;
  missingTokens: string[];
  renderedSubject: string;
  renderedBodyHtml: string | null;
}

export interface CampaignAddResult {
  added: number;
  alreadyInCampaign: number;
  notAllowed: number;
  total: number;
  atCapacity: boolean;
}

export interface CampaignToken {
  token: string;
  label: string;
}

export interface CampaignSettings {
  enabled: boolean;
  maxRecipients: number;
  sendRatePerMinute: number;
}

export type CreateCampaignInput = {
  name: string;
  subject?: string;
  bodyHtml?: string;
  sourceTemplateId?: string | null;
  projectTicketId?: string | null;
  defaultProposedUtc?: string | null;
  addProjectTickets?: boolean;
  ticketIds?: string[];
};

export type UpdateCampaignInput = Partial<{
  name: string;
  subject: string;
  bodyHtml: string;
  sourceTemplateId: string | null;
  projectTicketId: string | null;
  clearProject: boolean;
  defaultProposedUtc: string | null;
  clearDefaultProposed: boolean;
  state: CampaignState;
  otherTimeNotice: string;
}>;

export type StartRoundInput = {
  kind: CampaignRoundKind;
  pendingStatusId?: string | null;
  pendingTillUtc?: string | null;
  ticketIds?: string[];
  /** Phase 2 — "send on …" (UTC ISO, server-checked); omit to send now. */
  scheduledUtc?: string | null;
};

/// Phase 2 — the public answer page (no session; the token is the credential).
export interface CampaignAnswerView {
  status: "open";
  campaignName: string;
  ticketNumber: number;
  proposedText: string | null;
  expiresUtc: string;
  /** Server-formatted (server zone, campaign date language). */
  expiresText: string | null;
  answer: CampaignDecision | null;
  note: string | null;
  answeredUtc: string | null;
  answeredText: string | null;
  /** The campaign's note for "Another moment" (plain text, may be empty). */
  otherTimeNotice: string;
  /** Bounds of the date picker: server-local calendar dates ("yyyy-MM-dd"). */
  preferredMinDate: string;
  preferredMaxDate: string;
  preferredDate: string | null;
  preferredMinute: number | null;
  /** Server-formatted preferred moment (campaign date language). */
  preferredText: string | null;
}

export const publicCampaignAnswersApi = {
  get: (token: string) => request<CampaignAnswerView>("GET", `/api/campaign-answers/${encodeURIComponent(token)}`),
  submit: (token: string, choice: CampaignDecision, note: string, preferredDate: string | null, preferredMinute: number | null) =>
    request<CampaignAnswerView>("POST", `/api/campaign-answers/${encodeURIComponent(token)}`,
      { choice, note, preferredDate, preferredMinute }),
};

export const CAMPAIGNS_KEY = ["campaigns"] as const;
export const campaignKeys = {
  list: (includeArchived: boolean) => [...CAMPAIGNS_KEY, "list", includeArchived] as const,
  forProject: (projectTicketId: string) => [...CAMPAIGNS_KEY, "project", projectTicketId] as const,
  detail: (id: string) => [...CAMPAIGNS_KEY, "detail", id] as const,
  plan: (id: string, kind: CampaignRoundKind) => [...CAMPAIGNS_KEY, "plan", id, kind] as const,
  preview: (id: string, ticketId: string) => [...CAMPAIGNS_KEY, "preview", id, ticketId] as const,
  tokens: () => [...CAMPAIGNS_KEY, "tokens"] as const,
  settings: () => [...CAMPAIGNS_KEY, "settings"] as const,
};

const enc = encodeURIComponent;

export const campaignsApi = {
  list: (includeArchived = false, projectTicketId?: string) => {
    const s = new URLSearchParams();
    if (includeArchived) s.set("includeArchived", "true");
    if (projectTicketId) s.set("projectTicketId", projectTicketId);
    const q = s.toString();
    return request<{ items: CampaignListItem[] }>("GET", `/api/campaigns/${q ? `?${q}` : ""}`).then((r) => r.items);
  },
  get: (id: string) => request<CampaignDetail>("GET", `/api/campaigns/${enc(id)}`),
  create: (input: CreateCampaignInput) =>
    request<{ id: string; added: CampaignAddResult | null }>("POST", "/api/campaigns/", input),
  update: (id: string, input: UpdateCampaignInput) =>
    request<void>("PATCH", `/api/campaigns/${enc(id)}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/campaigns/${enc(id)}`),
  duplicate: (id: string, name?: string) =>
    request<{ id: string; added: CampaignAddResult }>("POST", `/api/campaigns/${enc(id)}/duplicate`, { name }),
  addTickets: (id: string, ticketIds: string[]) =>
    request<CampaignAddResult>("POST", `/api/campaigns/${enc(id)}/tickets`, { ticketIds }),
  addProjectTickets: (id: string) =>
    request<CampaignAddResult>("POST", `/api/campaigns/${enc(id)}/tickets/from-project`),
  removeTickets: (id: string, ticketIds: string[]) =>
    request<{ removed: number; kept: number }>("POST", `/api/campaigns/${enc(id)}/tickets/remove`, { ticketIds }),
  setProposed: (id: string, ticketIds: string[], proposedUtc: string | null) =>
    request<{ updated: number }>("PUT", `/api/campaigns/${enc(id)}/tickets/proposed`, { ticketIds, proposedUtc }),
  setDecision: (id: string, ticketId: string, decision: CampaignDecision | null) =>
    request<void>("PUT", `/api/campaigns/${enc(id)}/tickets/${enc(ticketId)}/decision`, { decision }),
  preview: (id: string, ticketId: string) =>
    request<CampaignPlanItem>("GET", `/api/campaigns/${enc(id)}/preview/${enc(ticketId)}`),
  plan: (id: string, kind: CampaignRoundKind, ticketIds?: string[]) =>
    request<{ items: CampaignPlanItem[] }>("POST", `/api/campaigns/${enc(id)}/plan`, { kind, ticketIds }).then((r) => r.items),
  startRound: (id: string, input: StartRoundInput) =>
    request<{ roundId: string; roundNo: number; queued: number; skipped: number }>(
      "POST", `/api/campaigns/${enc(id)}/rounds`, input),
  cancelQueued: (id: string) => request<{ cancelled: number }>("POST", `/api/campaigns/${enc(id)}/cancel-queued`),
  retryFailed: (id: string) => request<{ retried: number }>("POST", `/api/campaigns/${enc(id)}/retry-failed`),
  tokens: () => request<{ tokens: CampaignToken[] }>("GET", "/api/campaigns/tokens").then((r) => r.tokens),
  settings: () => request<CampaignSettings>("GET", "/api/settings/campaigns"),
};

/// Copy for the stable skip-reason codes (server: CampaignSkipReasons +
/// TicketBulkSkipReason for the pending step).
export const CAMPAIGN_SKIP_REASON_LABELS: Record<string, string> = {
  no_access: "No access to this ticket's queue",
  project_ticket: "Project ticket — projects never mail customers",
  closed: "Ticket is closed",
  merged: "Ticket was merged into another ticket",
  no_requester_email: "The requester has no e-mail address",
  missing_tokens: "A placeholder has no value for this ticket",
  sender_not_allowed: "The sender no longer has Campaigns access",
  no_mailbox: "The ticket's queue has no outbound mailbox",
  requester_changed: "The requester changed after the round was queued",
  invalid: "The ticket no longer exists",
  answer_link_unavailable: "The answer link needs the app's public address (Settings → Mail → Microsoft 365 sign-in → App.PublicBaseUrl)",
  proposed_date_passed: "The proposed date has already passed",
  status_gate_required: "A status gate needs confirmation on the ticket",
  checklist_incomplete: "A checklist blocks this status",
  status_not_in_queue_scope: "Status not allowed in this queue",
  pending_till_in_past: "Pending date had already passed",
  pending_till_not_pending: "The status is not a Pending status",
  failed: "Failed",
};

export function skipReasonLabel(code: string | null | undefined): string {
  if (!code) return "";
  return CAMPAIGN_SKIP_REASON_LABELS[code] ?? code;
}

export const CAMPAIGN_STATE_LABELS: Record<CampaignRecipientState, string> = {
  NotSent: "Not sent",
  Queued: "Queued",
  Failed: "Failed",
  Skipped: "Skipped",
  ReplyReceived: "Reply received",
  TimerExpired: "Timer expired",
  Waiting: "Waiting for reply",
};

export const CAMPAIGN_DECISION_LABELS: Record<CampaignDecision, string> = {
  Approved: "Approved",
  OtherTime: "Other time",
  Declined: "Declined",
};
