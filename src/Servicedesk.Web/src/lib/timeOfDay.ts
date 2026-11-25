/// Time of day as minutes after midnight (server-local wall time) ↔ "09:00".
/// Pure helpers — no clock involved.
export function minuteToTime(minute: number): string {
  if (!Number.isFinite(minute)) return "—";
  const h = Math.floor(minute / 60);
  const m = minute % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

export function timeToMinute(value: string): number | null {
  const match = /^(\d{1,2}):(\d{2})$/.exec(value.trim());
  if (!match) return null;
  const h = Number(match[1]);
  const m = Number(match[2]);
  return h < 24 && m < 60 ? h * 60 + m : null;
}
