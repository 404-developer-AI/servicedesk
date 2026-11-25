import { request } from "@/lib/http";

/// v0.1.67 (Campaigns phase 3) — the Maintenance planner: one plan = a
/// recurring intervention for many customers (members). On the mail day
/// (intervention date − days before) every member gets a NEW ticket in the
/// round's campaign, sent automatically when the plan says so. Same access
/// as Campaigns (per-user `campaigns_enabled`, server-enforced). Dates are
/// server-local calendar dates ("yyyy-MM-dd").

export type ScheduleMode = "Manual" | "Automatic";

export interface MaintenanceRoundSummary {
  id: string;
  interventionDate: string | null;
  state: "Pending" | "Created" | "Failed" | null;
  error: string | null;
  campaignId: string | null;
  campaignName: string | null;
  tickets: number;
  sent: number;
  approved: number;
  otherTime: number;
  declined: number;
  skipped: number;
  /** When the round's reminder was handled, and what happened. */
  reminderUtc: string | null;
  reminderNote: string | null;
}

export interface MaintenancePlan {
  id: string;
  name: string;
  notes: string;
  queueId: string;
  queueName: string | null;
  ticketTemplateId: string | null;
  ticketTemplateName: string | null;
  ticketSubject: string;
  mailSubject: string;
  mailBodyHtml: string;
  scheduleMode: ScheduleMode;
  intervalMonths: number | null;
  nextInterventionDate: string | null;
  interventionMinute: number;
  mailDaysBefore: number;
  /** Server-local time of day the round starts on the mail day. */
  mailMinute: number;
  /** Copied onto each round's campaign: shown on the answer page with "Another moment". */
  otherTimeNotice: string;
  /** One reminder per round, N days after the first mail (auto-send only). */
  reminderEnabled: boolean;
  reminderDays: number;
  reminderSubject: string;
  reminderBodyHtml: string;
  autoSend: boolean;
  sendAsEmail: string | null;
  pendingStatusId: string | null;
  /** null = wait until the intervention; N = N days after the mail (capped at the intervention). */
  pendingDays: number | null;
  isActive: boolean;
  memberCount: number;
  lastRound: MaintenanceRoundSummary | null;
}

export interface MaintenanceMember {
  id: string | null;
  companyId: string | null;
  companyName?: string | null;
  contactId: string;
  contactName?: string | null;
  contactEmail?: string | null;
  label: string;
  isActive: boolean;
  /** Leave this member out of the next round only; cleared when that round starts. */
  skipNextRound: boolean;
  /** Maintenance notes of this member (read-only; edited through the notes panel). */
  noteCount?: number;
}

export type MaintenancePlanInput = Omit<
  MaintenancePlan,
  "id" | "queueName" | "ticketTemplateName" | "sendAsEmail" | "memberCount" | "lastRound"
> & { members: MaintenanceMember[] };

export type TokenInfo = { token: string; label: string };

export const MAINTENANCE_KEY = ["maintenance"] as const;
export const maintenanceKeys = {
  plans: () => [...MAINTENANCE_KEY, "plans"] as const,
  plan: (id: string) => [...MAINTENANCE_KEY, "plan", id] as const,
  tokens: () => [...MAINTENANCE_KEY, "tokens"] as const,
};

const enc = encodeURIComponent;

export const maintenanceApi = {
  plans: () => request<{ items: MaintenancePlan[] }>("GET", "/api/maintenance/plans?includeInactive=true").then((r) => r.items),
  plan: (id: string) =>
    request<{ plan: MaintenancePlan; members: MaintenanceMember[] }>("GET", `/api/maintenance/plans/${enc(id)}`),
  createPlan: (input: MaintenancePlanInput) => request<{ id: string }>("POST", "/api/maintenance/plans", input),
  updatePlan: (id: string, input: MaintenancePlanInput) => request<void>("PUT", `/api/maintenance/plans/${enc(id)}`, input),
  deletePlan: (id: string) => request<void>("DELETE", `/api/maintenance/plans/${enc(id)}`),
  startNow: (id: string) => request<void>("POST", `/api/maintenance/plans/${enc(id)}/start-now`),
  tokens: () => request<{ ticket: TokenInfo[]; mail: TokenInfo[] }>("GET", "/api/maintenance/tokens"),
};

/// The mail day of an intervention: the calendar date N days earlier ("yyyy-MM-dd").
export function mailDateOf(interventionDate: string | null, daysBefore: number): string | null {
  if (!interventionDate) return null;
  const [y, m, d] = interventionDate.split("-").map(Number);
  const day = new Date(Date.UTC(y, m - 1, d - daysBefore));
  return day.toISOString().slice(0, 10);
}

/// "Every 6 months" / "Manual".
export function scheduleLabel(p: Pick<MaintenancePlan, "scheduleMode" | "intervalMonths">): string {
  return p.scheduleMode === "Automatic" && p.intervalMonths
    ? `Every ${p.intervalMonths} month${p.intervalMonths === 1 ? "" : "s"}`
    : "Manual";
}
