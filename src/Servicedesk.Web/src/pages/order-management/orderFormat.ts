import type { TdOrderHeader, TdOrderStatus } from "@/lib/order-management-api";

/// Pure helpers shared by the Order Management UI (dashboard, detail,
/// ticket chip). Components live in orderUi.tsx.

export type Tone = "neutral" | "info" | "progress" | "success" | "danger" | "warning";

/// Light + dark variants per tone — the same palette the app already uses
/// for status chips (amber/sky/emerald/rose/violet with a soft fill).
export const TONE_CLASSES: Record<Tone, string> = {
  neutral: "border-glass-strong bg-glass text-muted-foreground",
  info: "border-sky-400/50 bg-sky-100/70 text-sky-900 dark:border-sky-400/30 dark:bg-sky-400/10 dark:text-sky-200",
  progress: "border-violet-400/50 bg-violet-100/70 text-violet-900 dark:border-violet-400/30 dark:bg-violet-400/10 dark:text-violet-200",
  success: "border-emerald-400/50 bg-emerald-100/70 text-emerald-900 dark:border-emerald-400/30 dark:bg-emerald-400/10 dark:text-emerald-200",
  danger: "border-rose-400/50 bg-rose-100/70 text-rose-900 dark:border-rose-400/30 dark:bg-rose-400/10 dark:text-rose-200",
  warning: "border-amber-400/50 bg-amber-100/70 text-amber-900 dark:border-amber-400/30 dark:bg-amber-400/12 dark:text-amber-200",
};

const STATUS_META: Record<string, { label: string; tone: Tone }> = {
  OPEN: { label: "Open", tone: "info" },
  INVESTIGATION: { label: "Investigation", tone: "warning" },
  SHIPPING: { label: "Shipping", tone: "progress" },
  COMPLETED: { label: "Completed", tone: "success" },
  REJECTED: { label: "Rejected", tone: "danger" },
};

export function statusMeta(status: TdOrderStatus | null | undefined): { label: string; tone: Tone } {
  if (!status) return { label: "Unknown", tone: "neutral" };
  return STATUS_META[status.toUpperCase()] ?? { label: status, tone: "neutral" };
}

/// The order's tone, with "late" winning over the status colour.
export function orderTone(o: Pick<TdOrderHeader, "status" | "isLate">): Tone {
  return o.isLate ? "danger" : statusMeta(o.status).tone;
}

/// "2026-11-27" → "27/11/2026". Ship dates are calendar dates (no time, no
/// zone), so they are formatted from the string — never through Date.
export function formatShipDate(iso: string | null | undefined, fallback = "—"): string {
  if (!iso) return fallback;
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso);
  return m ? `${m[3]}/${m[2]}/${m[1]}` : iso;
}

export function formatQty(n: number | null | undefined): string {
  if (n === null || n === undefined) return "—";
  return Number(n).toLocaleString("nl-BE", { maximumFractionDigits: 2 });
}
