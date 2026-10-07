import { formatDayMonthTime, formatTime } from "@/lib/dateFormat";
import type { CategoryScore, Severity } from "@/lib/perf-api";

const nf0 = new Intl.NumberFormat("en-GB", { maximumFractionDigits: 0 });
const nf1 = new Intl.NumberFormat("en-GB", { maximumFractionDigits: 1 });

export function fmtCount(n: number | null | undefined): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return "—";
  return nf0.format(n);
}

export function fmtMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) return "—";
  if (ms >= 10_000) return `${(ms / 1000).toFixed(1)} s`;
  if (ms >= 1000) return `${(ms / 1000).toFixed(2)} s`;
  if (ms >= 10) return `${Math.round(ms)} ms`;
  return `${ms.toFixed(1)} ms`;
}

export function fmtDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) return "—";
  if (ms < 1000) return fmtMs(ms);
  const s = ms / 1000;
  if (s < 120) return `${s.toFixed(1)} s`;
  const m = s / 60;
  if (m < 120) return `${m.toFixed(1)} min`;
  return `${(m / 60).toFixed(1)} h`;
}

export function fmtPct(p: number | null | undefined, digits?: number): string {
  if (p === null || p === undefined || !Number.isFinite(p)) return "—";
  const d = digits ?? (Math.abs(p) < 10 ? 1 : 0);
  return `${p.toFixed(d)}%`;
}

export function fmtBytes(b: number | null | undefined): string {
  if (b === null || b === undefined || !Number.isFinite(b)) return "—";
  const abs = Math.abs(b);
  if (abs >= 1024 ** 3) return `${(b / 1024 ** 3).toFixed(1)} GB`;
  if (abs >= 1024 ** 2) return `${(b / 1024 ** 2).toFixed(1)} MB`;
  if (abs >= 1024) return `${nf0.format(b / 1024)} KB`;
  return `${nf0.format(b)} B`;
}

export function fmtNum(n: number | null | undefined, digits = 1): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return "—";
  return digits === 1 ? nf1.format(n) : n.toFixed(digits);
}

/** Server UTC timestamp → local short time/date for chart axes and tables. */
export function fmtTime(iso: string, withDate = false): string {
  if (Number.isNaN(new Date(iso).getTime())) return iso;
  return withDate ? formatDayMonthTime(iso) : formatTime(iso);
}

export function fmtAgo(iso: string | null | undefined, nowMs: number): string {
  if (!iso) return "never";
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return "—";
  const s = Math.max(0, (nowMs - t) / 1000);
  if (s < 90) return `${Math.round(s)} s ago`;
  if (s < 5400) return `${Math.round(s / 60)} min ago`;
  if (s < 172800) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} days ago`;
}

/// Status tones. Text tokens differ per mode so the label stays readable on
/// Steaan's light surfaces and Nebula's dark glass alike.
export const TONE = {
  good: {
    badge: "border-emerald-600/25 bg-emerald-500/10 text-emerald-700 dark:border-emerald-400/30 dark:text-emerald-300",
    dot: "bg-emerald-500",
    text: "text-emerald-700 dark:text-emerald-300",
  },
  warning: {
    badge: "border-amber-600/30 bg-amber-500/10 text-amber-800 dark:border-amber-400/30 dark:text-amber-300",
    dot: "bg-amber-500",
    text: "text-amber-700 dark:text-amber-300",
  },
  critical: {
    badge: "border-rose-600/30 bg-rose-500/10 text-rose-700 dark:border-rose-400/40 dark:text-rose-300",
    dot: "bg-rose-500",
    text: "text-rose-700 dark:text-rose-300",
  },
  info: {
    badge: "border-sky-600/25 bg-sky-500/10 text-sky-800 dark:border-sky-400/30 dark:text-sky-300",
    dot: "bg-sky-500",
    text: "text-sky-700 dark:text-sky-300",
  },
  nodata: {
    badge: "border-glass bg-glass text-muted-foreground",
    dot: "bg-muted-foreground/40",
    text: "text-muted-foreground",
  },
} as const;

export type Tone = keyof typeof TONE;

export function severityTone(s: Severity): Tone {
  return s === "critical" ? "critical" : s === "warning" ? "warning" : "info";
}

export function categoryTone(c: CategoryScore["status"]): Tone {
  return c;
}

/// Google's Core Web Vitals bands (good / needs improvement / poor).
export function vitalTone(metric: "lcp" | "inp" | "cls" | "fcp" | "ttfb", value: number | null | undefined): Tone {
  if (value === null || value === undefined) return "nodata";
  const bands: Record<string, [number, number]> = {
    lcp: [2500, 4000],
    inp: [200, 500],
    cls: [0.1, 0.25],
    fcp: [1800, 3000],
    ttfb: [800, 1800],
  };
  const [good, poor] = bands[metric];
  return value <= good ? "good" : value <= poor ? "warning" : "critical";
}

/// Latency tone for a route's p95 against the slow-request threshold.
export function latencyTone(ms: number, slowMs: number): Tone {
  if (ms <= slowMs * 0.3) return "good";
  if (ms <= slowMs) return "warning";
  return "critical";
}
