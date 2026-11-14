import { serverNowMs } from "@/hooks/useServerTime";
import { formatDate } from "@/lib/dateFormat";

/// Shared display formatters. Use these instead of a local copy so the same
/// value reads the same on every screen. Dates/times themselves go through
/// `lib/dateFormat.ts`; "now" is always server time (`serverNowMs`).

const EMPTY = "—";

/// File size: "512 B", "3.4 KB", "1.2 MB", "2.05 GB". Empty for invalid input.
export function formatBytes(n: number): string {
  if (!Number.isFinite(n) || n < 0) return "";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function toMs(value: string | number): number {
  return typeof value === "number" ? value : Date.parse(value);
}

export type TimeAgoOptions = {
  /// Returned for null/empty/unparseable input. Default "—".
  fallback?: string;
  /// From this many days on, show the date instead ("12 Mar 2026").
  dateAfterDays?: number;
};

/// Past moment relative to server now: "just now", "5m ago", "3h ago",
/// "2d ago". Pure — re-render via `useServerNowMs()` where it must tick.
export function formatTimeAgo(value: string | number | null | undefined, opts: TimeAgoOptions = {}): string {
  const fallback = opts.fallback ?? EMPTY;
  if (value === null || value === undefined || value === "") return fallback;
  const then = toMs(value);
  if (Number.isNaN(then)) return fallback;
  const mins = Math.floor((serverNowMs() - then) / 60_000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (opts.dateAfterDays !== undefined && days >= opts.dateAfterDays) return formatDate(then);
  return `${days}d ago`;
}

/// Signed distance to a moment (target − now, in ms): "in 5m" / "5m ago",
/// "in 3h" / "3h ago"; under a minute: "now".
export function formatTimeFromNow(deltaMs: number): string {
  const abs = Math.abs(deltaMs);
  if (abs < 60_000) return "now";
  const future = deltaMs > 0;
  const say = (n: number, unit: string) => (future ? `in ${n}${unit}` : `${n}${unit} ago`);
  const mins = Math.round(abs / 60_000);
  if (mins < 60) return say(mins, "m");
  const hours = Math.round(mins / 60);
  if (hours < 24) return say(hours, "h");
  return say(Math.round(hours / 24), "d");
}

/// Exact duration in minutes: "45m", "2h", "2h 30m". `empty` for ≤ 0 or
/// missing (default "0m").
export function formatDuration(minutes: number | null | undefined, empty = "0m"): string {
  if (minutes === null || minutes === undefined || !Number.isFinite(minutes) || minutes <= 0) return empty;
  const total = Math.round(minutes);
  const h = Math.floor(total / 60);
  const m = total % 60;
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}h`;
  return `${h}h ${m}m`;
}

/// Approximate duration for averages/KPIs: "45m", "1.5h", "2.0d".
export function formatApproxMinutes(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined || !Number.isFinite(minutes)) return EMPTY;
  if (minutes < 60) return `${Math.round(minutes)}m`;
  const h = minutes / 60;
  if (h < 24) return `${h.toFixed(1)}h`;
  return `${(h / 24).toFixed(1)}d`;
}

/// Amount in the given currency (default EUR), Belgian number style.
export function formatMoney(value: number | null | undefined, currency?: string | null): string {
  if (value === null || value === undefined) return EMPTY;
  try {
    return new Intl.NumberFormat("nl-BE", { style: "currency", currency: currency || "EUR" }).format(value);
  } catch {
    return value.toFixed(2);
  }
}

/// Decimal without trailing zeros ("1.00000000" → "1", "3.5"), up to 4 places.
export function formatNumber(value: number | null | undefined): string {
  if (value === null || value === undefined) return EMPTY;
  return Number(value).toLocaleString("nl-BE", { maximumFractionDigits: 4 });
}

/// Escapes text for safe interpolation into an HTML string.
export function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
