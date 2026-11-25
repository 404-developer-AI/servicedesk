import type { CampaignRecipient } from "@/lib/campaigns-api";

/// v0.1.67 — the dashboard's filter chips over the derived recipient state.
/// "Needs attention" = an answer or an expired timer without a decision yet.
export type RecipientFilter = "attention" | "waiting" | "notSent" | "problems" | "decided" | "all";

export const RECIPIENT_FILTERS: { id: RecipientFilter; label: string; match: (r: CampaignRecipient) => boolean }[] = [
  {
    id: "attention",
    label: "Needs attention",
    match: (r) => !r.decision && (r.state === "ReplyReceived" || r.state === "TimerExpired"),
  },
  { id: "waiting", label: "Waiting for reply", match: (r) => !r.decision && (r.state === "Waiting" || r.state === "Queued") },
  { id: "notSent", label: "Not sent", match: (r) => r.state === "NotSent" },
  { id: "problems", label: "Failed / skipped", match: (r) => r.state === "Failed" || r.state === "Skipped" },
  { id: "decided", label: "Decided", match: (r) => !!r.decision },
  { id: "all", label: "All", match: () => true },
];

export function filterRecipients(rows: CampaignRecipient[], filter: RecipientFilter): CampaignRecipient[] {
  const f = RECIPIENT_FILTERS.find((x) => x.id === filter) ?? RECIPIENT_FILTERS[RECIPIENT_FILTERS.length - 1];
  return rows.filter(f.match);
}
