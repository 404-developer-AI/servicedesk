import { formatDateMedium, formatTime } from "@/lib/dateFormat";

/// Compact "how long ago" for list rows, measured against the SERVER clock
/// (useServerNowMs) — never the phone's clock. Older than a week falls back
/// to the server-zone date.
export function relativeAge(iso: string, serverNowMs: number | null, zone: string | null): string {
  const then = Date.parse(iso);
  if (!Number.isFinite(then)) return "—";
  if (serverNowMs === null) return formatDateMedium(iso, zone);
  const minutes = Math.floor((serverNowMs - then) / 60_000);
  if (minutes < 1) return "now";
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d`;
  return formatDateMedium(iso, zone);
}

/// Timeline stamp: today → "14:05", otherwise "07 Oct 2026, 14:05".
export function eventStamp(iso: string, serverNowMs: number | null, zone: string | null): string {
  const then = Date.parse(iso);
  if (serverNowMs !== null && Number.isFinite(then) && serverNowMs - then < 20 * 3_600_000
    && formatDateMedium(iso, zone) === formatDateMedium(new Date(serverNowMs).toISOString(), zone)) {
    return formatTime(iso, zone);
  }
  return `${formatDateMedium(iso, zone)}, ${formatTime(iso, zone)}`;
}

/// "jan.peeters@x.be" → "jan.peeters" — the same label the @@-picker uses.
export function emailLocalPart(email: string | null | undefined): string {
  if (!email) return "";
  const at = email.indexOf("@");
  return at > 0 ? email.slice(0, at) : email;
}

export function requesterName(first: string | null | undefined, last: string | null | undefined, email: string | null | undefined): string {
  const name = [first, last].filter((s) => s && s.trim().length > 0).join(" ").trim();
  return name.length > 0 ? name : email ?? "";
}
