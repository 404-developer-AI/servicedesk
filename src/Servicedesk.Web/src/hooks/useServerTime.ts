import { useEffect, useState } from "react";
import { systemApi, type SystemTime } from "@/lib/api";

// TODO(settings): interval should be a setting once the settings store exists.
const POLL_INTERVAL_MS = 1000;

export type ServerTime = SystemTime & {
  /** Server local time (as a Date interpreted in UTC fields). */
  serverLocal: Date;
};

function toServerLocal(time: SystemTime): Date {
  const utcMs = new Date(time.utc).getTime();
  return new Date(utcMs + time.offsetMinutes * 60_000);
}

export function formatServerLocalClock(time: ServerTime): string {
  const d = time.serverLocal;
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
}

export function formatServerLocalDate(time: ServerTime): string {
  const d = time.serverLocal;
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
}

export function useServerTime() {
  const [time, setTime] = useState<ServerTime | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const tick = () => {
      systemApi
        .time()
        .then((t) => {
          if (cancelled) return;
          setTime({ ...t, serverLocal: toServerLocal(t) });
          setError(null);
        })
        .catch((e: unknown) => {
          if (!cancelled) setError(e instanceof Error ? e.message : String(e));
        });
    };
    tick();
    const id = window.setInterval(tick, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      window.clearInterval(id);
    };
  }, []);

  return { time, error };
}
