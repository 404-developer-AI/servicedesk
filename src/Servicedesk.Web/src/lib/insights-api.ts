// v0.1.13 — Insights reporting dashboard. Every endpoint is gated per user
// by `insights_enabled` and scoped to the caller's queue access server-side.

import { ApiError } from "@/lib/ticket-api";

export type InsightsPeriod = "today" | "week" | "month" | "year" | "custom";
export type InsightsGranularity = "day" | "week" | "month" | "year";

/** Which overview: counted on creation, or on the close moment. */
export type InsightsReportKind = "new-tickets" | "closed-tickets";
/** Outcome filter for the closed-tickets overview. */
export type ClosedOutcome = "resolved" | "closed" | "merged";
export const ALL_OUTCOMES: readonly ClosedOutcome[] = ["resolved", "closed", "merged"];

export type InsightsConfig = {
  defaultPeriod: Exclude<InsightsPeriod, "custom">;
  timeZone: string;
  /** Server-local date (yyyy-MM-dd) in the display time zone. */
  today: string;
  maxBuckets: number;
};

export type InsightsQueue = {
  id: string;
  name: string;
  isActive: boolean;
  /** Fixed position in the caller's queue list — picks the chart colour. */
  slot: number;
};

export type TicketCountBucket = {
  /** Calendar period key (Monday / 1st / Jan 1). */
  start: string;
  /** Part of the period inside the range (first/last can be partial). */
  from: string;
  to: string;
  /** Aligned index-for-index with `queues`. */
  counts: number[];
};

export type TicketCountReport = {
  from: string;
  to: string;
  granularity: InsightsGranularity;
  timeZone: string;
  today: string;
  queues: InsightsQueue[];
  buckets: TicketCountBucket[];
};

export type InsightsTicketItem = {
  id: string;
  number: number;
  subject: string;
  requester: string;
  company: string | null;
  statusName: string;
  statusColor: string;
  statusCategory: string;
  /** The moment the ticket was counted on (created or closed). */
  momentUtc: string;
};

export type InsightsTicketPage = { total: number; items: InsightsTicketItem[] };

export type ReportParams = {
  period: InsightsPeriod;
  offset: number;
  from?: string;
  to?: string;
  granularity?: InsightsGranularity;
  /** Closed-tickets only; all outcomes when omitted. */
  outcomes?: readonly ClosedOutcome[];
};

function toQuery(p: ReportParams, queueIds?: string[]): URLSearchParams {
  const qs = new URLSearchParams({ period: p.period });
  if (p.period === "custom") {
    if (p.from) qs.set("from", p.from);
    if (p.to) qs.set("to", p.to);
  } else if (p.offset !== 0) {
    qs.set("offset", String(p.offset));
  }
  if (p.granularity) qs.set("granularity", p.granularity);
  if (p.outcomes && p.outcomes.length < ALL_OUTCOMES.length) qs.set("outcomes", p.outcomes.join(","));
  for (const id of queueIds ?? []) qs.append("queueIds", id);
  return qs;
}

async function fail(res: Response, url: string): Promise<never> {
  let body: unknown = null;
  try {
    const txt = await res.text();
    if (txt) body = JSON.parse(txt);
  } catch {
    // non-JSON body
  }
  throw new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`, body);
}

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url, { credentials: "include", headers: { Accept: "application/json" } });
  if (!res.ok) await fail(res, url);
  return (await res.json()) as T;
}

export const insightsApi = {
  config: () => getJson<InsightsConfig>("/api/insights/config"),

  report: (kind: InsightsReportKind, p: ReportParams) =>
    getJson<TicketCountReport>(`/api/insights/${kind}?${toQuery(p).toString()}`),

  /** The tickets behind a report for the ticked queues (empty = all),
   *  newest first, one page at a time. */
  tickets: (kind: InsightsReportKind, p: ReportParams, queueIds: string[], offset: number, limit: number) => {
    const qs = toQuery(p, queueIds);
    qs.set("listOffset", String(offset));
    qs.set("limit", String(limit));
    return getJson<InsightsTicketPage>(`/api/insights/${kind}/tickets?${qs.toString()}`);
  },

  /** Downloads the PDF for exactly the current filters. An empty
   *  `queueIds` means "all queues". */
  async downloadPdf(kind: InsightsReportKind, p: ReportParams, queueIds: string[]): Promise<void> {
    const url = `/api/insights/${kind}/pdf?${toQuery(p, queueIds).toString()}`;
    const res = await fetch(url, { credentials: "include", headers: { Accept: "application/pdf" } });
    if (!res.ok) await fail(res, url);

    const blob = await res.blob();
    const disposition = res.headers.get("Content-Disposition") ?? "";
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    const name = match ? decodeURIComponent(match[1]) : `insights-${kind}.pdf`;

    const href = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = href;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    window.setTimeout(() => URL.revokeObjectURL(href), 1000);
  },
};

/** Server-sent error message from a 400, when there is one. */
export function insightsErrorMessage(err: unknown): string | null {
  if (err instanceof ApiError && err.body && typeof err.body === "object" && "error" in err.body) {
    const msg = (err.body as { error?: unknown }).error;
    return typeof msg === "string" ? msg : null;
  }
  return null;
}
