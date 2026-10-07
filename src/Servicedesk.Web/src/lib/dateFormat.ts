import { getServerTimeSnapshot } from "@/hooks/useServerTime";

/// Shared, locale-independent date formatting. The UI is English and every
/// timestamp is shown in the server's display time zone (App.TimeZone), never
/// the browser's locale or zone — `toLocaleString()` / `Intl.DateTimeFormat
/// (undefined, …)` rendered "07 okt 2026" for Dutch browsers and shifted
/// times for agents abroad.
///
/// Each formatter takes an optional IANA zone. Components should pass the
/// value of `useServerTimeZone()` so they re-render once the first sync lands;
/// when omitted the current server snapshot is used, and before the first
/// sync the formatter falls back to the browser zone (display-only — nothing
/// is ever scheduled off it).
///
/// Date-only values ("2026-10-07", e.g. Adsolut document dates) are calendar
/// dates without a zone and are formatted as-is, never shifted.

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;
const LOCALE = "en-GB";

const MONTHS_SHORT = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const MONTHS_LONG = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December",
];

type Input = string | number | Date | null | undefined;

type Parts = { y: number; m: number; d: number; hh: number; mi: number; ss: number };

const formatterCache = new Map<string, Intl.DateTimeFormat>();

function zoneFormatter(zone: string | undefined): Intl.DateTimeFormat {
  const key = zone ?? "";
  let f = formatterCache.get(key);
  if (!f) {
    try {
      f = new Intl.DateTimeFormat(LOCALE, {
        timeZone: zone,
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
        hourCycle: "h23",
      });
    } catch {
      // Unknown zone id — fall back to the browser zone rather than throw
      // inside a render.
      f = new Intl.DateTimeFormat(LOCALE, {
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
        hourCycle: "h23",
      });
    }
    formatterCache.set(key, f);
  }
  return f;
}

function resolveZone(zone: string | null | undefined): string | undefined {
  return zone ?? getServerTimeSnapshot()?.timezone ?? undefined;
}

function toParts(value: Input, zone: string | null | undefined): Parts | null {
  if (value === null || value === undefined || value === "") return null;
  if (typeof value === "string" && DATE_ONLY.test(value)) {
    const [y, m, d] = value.split("-").map(Number);
    return { y, m, d, hh: 0, mi: 0, ss: 0 };
  }
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return null;
  const parts = zoneFormatter(resolveZone(zone)).formatToParts(date);
  const get = (t: Intl.DateTimeFormatPartTypes) =>
    Number(parts.find((p) => p.type === t)?.value ?? 0);
  return { y: get("year"), m: get("month"), d: get("day"), hh: get("hour"), mi: get("minute"), ss: get("second") };
}

const pad = (n: number) => String(n).padStart(2, "0");

/** "07/10/2026 - 20:52" (or "…:05" with seconds). Matches `toServerLocal`. */
export function formatDateTime(value: Input, zone?: string | null, seconds = false, fallback = "—"): string {
  const p = toParts(value, zone);
  if (!p) return fallback;
  const stamp = `${pad(p.d)}/${pad(p.m)}/${p.y} - ${pad(p.hh)}:${pad(p.mi)}`;
  return seconds ? `${stamp}:${pad(p.ss)}` : stamp;
}

/** "07/10/2026". */
export function formatDate(value: Input, zone?: string | null, fallback = "—"): string {
  const p = toParts(value, zone);
  return p ? `${pad(p.d)}/${pad(p.m)}/${p.y}` : fallback;
}

/** "07 Oct 2026" — for dense tables that previously used a month name. */
export function formatDateMedium(value: Input, zone?: string | null, fallback = "—"): string {
  const p = toParts(value, zone);
  return p ? `${pad(p.d)} ${MONTHS_SHORT[p.m - 1]} ${p.y}` : fallback;
}

/** "07 Oct 2026, 20:52". */
export function formatDateTimeMedium(value: Input, zone?: string | null, fallback = "—"): string {
  const p = toParts(value, zone);
  return p ? `${pad(p.d)} ${MONTHS_SHORT[p.m - 1]} ${p.y}, ${pad(p.hh)}:${pad(p.mi)}` : fallback;
}

/** "07 Oct, 20:52" — compact chart/tooltip label without the year. */
export function formatDayMonthTime(value: Input, zone?: string | null, fallback = "—"): string {
  const p = toParts(value, zone);
  return p ? `${pad(p.d)} ${MONTHS_SHORT[p.m - 1]}, ${pad(p.hh)}:${pad(p.mi)}` : fallback;
}

/** "20:52" (or "20:52:05"). */
export function formatTime(value: Input, zone?: string | null, seconds = false, fallback = "—"): string {
  const p = toParts(value, zone);
  if (!p) return fallback;
  return seconds ? `${pad(p.hh)}:${pad(p.mi)}:${pad(p.ss)}` : `${pad(p.hh)}:${pad(p.mi)}`;
}

/** "October 2026" from a 1-based month. Calendar value, no zone involved. */
export function formatMonthYear(year: number, month: number): string {
  return `${MONTHS_LONG[month - 1] ?? ""} ${year}`;
}

/**
 * The server's calendar date as "yyyy-MM-dd" (sortable, comparable with
 * date-only API values). `nowUtcMs` defaults to the server snapshot; null
 * before the first sync.
 */
export function serverTodayIso(zone?: string | null, nowUtcMs?: number | null): string | null {
  const snap = getServerTimeSnapshot();
  const ms = nowUtcMs ?? (snap ? new Date(snap.utc).getTime() : null);
  if (ms === null) return null;
  const p = toParts(ms, zone);
  return p ? `${p.y}-${pad(p.m)}-${pad(p.d)}` : null;
}
