import type { InsightsGranularity, TicketCountBucket, TicketCountReport } from "@/lib/insights-api";

// Validated categorical palette (8 slots, fixed order, CVD-checked on the
// adjacent pairs a stacked bar produces). Light steps match the PDF export
// exactly; dark steps are the same hues re-stepped for the Nebula dark
// surface. A queue's colour follows its slot, never its rank, so toggling
// queues never repaints the survivors. Slots past the eighth fold into one
// neutral tone instead of inventing indistinguishable hues.
const PALETTE_LIGHT = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948"];
const PALETTE_DARK = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];
const OVERFLOW = "#898781";

export function slotColor(slot: number, dark: boolean): string {
  const palette = dark ? PALETTE_DARK : PALETTE_LIGHT;
  return slot >= 0 && slot < palette.length ? palette[slot] : OVERFLOW;
}

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const MONTHS_LONG = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December",
];
const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

/** Parses a server yyyy-MM-dd as a calendar date (no time-zone shift). */
export function parseDate(s: string): { y: number; m: number; d: number; dow: number } {
  const [y, m, d] = s.split("-").map(Number);
  const dow = new Date(Date.UTC(y, m - 1, d)).getUTCDay();
  return { y, m, d, dow };
}

export function isoWeek(s: string): number {
  const { y, m, d } = parseDate(s);
  const date = new Date(Date.UTC(y, m - 1, d));
  const day = (date.getUTCDay() + 6) % 7;
  date.setUTCDate(date.getUTCDate() - day + 3); // Thursday of this week
  const firstThursday = new Date(Date.UTC(date.getUTCFullYear(), 0, 4));
  const diff = (date.getTime() - firstThursday.getTime()) / 86400000;
  return 1 + Math.round((diff - 3 + ((firstThursday.getUTCDay() + 6) % 7)) / 7);
}

export function fmtDate(s: string, withYear = false): string {
  const { y, m, d } = parseDate(s);
  return `${d} ${MONTHS[m - 1]}${withYear ? ` ${y}` : ""}`;
}

/** Same labels as the PDF export. */
export function bucketLabel(start: string, g: InsightsGranularity): string {
  const { y, m, d, dow } = parseDate(start);
  switch (g) {
    case "day":
      return `${DAYS[dow]} ${d} ${MONTHS[m - 1]}`;
    case "week":
      return `W${String(isoWeek(start)).padStart(2, "0")} · ${d} ${MONTHS[m - 1]}`;
    case "month":
      return `${MONTHS[m - 1]} ${y}`;
    case "year":
      return String(y);
  }
}

export function axisLabel(start: string, g: InsightsGranularity): string {
  const { y, m, d } = parseDate(start);
  switch (g) {
    case "day":
      return `${d} ${MONTHS[m - 1]}`;
    case "week":
      return `W${String(isoWeek(start)).padStart(2, "0")}`;
    case "month":
      return `${MONTHS[m - 1]} ${String(y).slice(2)}`;
    case "year":
      return String(y);
  }
}

/** Headline for the period stepper, e.g. "September 2026" or "Week 40". */
export function periodTitle(report: Pick<TicketCountReport, "from" | "to">, period: string): string {
  const f = parseDate(report.from);
  switch (period) {
    case "today":
      return `${DAYS[f.dow]} ${f.d} ${MONTHS[f.m - 1]} ${f.y}`;
    case "week":
      return `Week ${isoWeek(report.from)} · ${fmtDate(report.from)} – ${fmtDate(report.to, true)}`;
    case "month":
      return `${MONTHS_LONG[f.m - 1]} ${f.y}`;
    case "year":
      return String(f.y);
    default:
      return report.from === report.to
        ? fmtDate(report.from, true)
        : `${fmtDate(report.from, true)} – ${fmtDate(report.to, true)}`;
  }
}

export function isPartial(b: TicketCountBucket, g: InsightsGranularity): boolean {
  if (g === "day") return false;
  const { y, m, d } = parseDate(b.start);
  const start = new Date(Date.UTC(y, m - 1, d));
  const next = new Date(start);
  if (g === "week") next.setUTCDate(next.getUTCDate() + 7);
  if (g === "month") next.setUTCMonth(next.getUTCMonth() + 1);
  if (g === "year") next.setUTCFullYear(next.getUTCFullYear() + 1);
  next.setUTCDate(next.getUTCDate() - 1);
  const lastDay = next.toISOString().slice(0, 10);
  return b.from !== b.start || b.to !== lastDay;
}

export function daysBetweenInclusive(from: string, to: string): number {
  const a = parseDate(from);
  const b = parseDate(to);
  return Math.round((Date.UTC(b.y, b.m - 1, b.d) - Date.UTC(a.y, a.m - 1, a.d)) / 86400000) + 1;
}

export const nf = new Intl.NumberFormat("en-US");
