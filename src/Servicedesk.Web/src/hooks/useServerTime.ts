import { useEffect, useRef, useState } from "react";
import { systemApi, type SystemTime } from "@/lib/api";

// How often to re-sync with the server. The visible wall clock ticks locally
// every second between syncs — between syncs we just add elapsed ms to the
// last server snapshot, so the display is still server-anchored without
// hammering /api/system/time at 1Hz per open tab.
//
// TODO(settings): move to Ui.ServerTimeSync.IntervalSeconds once there is a
// UI to edit settings (backend store already exists — v0.0.7).
const RESYNC_INTERVAL_MS = 120_000;
const TICK_INTERVAL_MS = 1_000;

export type ServerTime = SystemTime & {
  /** Server local time (as a Date interpreted in UTC fields). */
  serverLocal: Date;
};

function utcMsToServerLocal(utcMs: number, offsetMinutes: number): Date {
  return new Date(utcMs + offsetMinutes * 60_000);
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

type Anchor = {
  /** Server UTC ms at the moment of the last successful sync. */
  serverUtcMs: number;
  /** performance.now() reading at the moment of the last successful sync. */
  monotonicMs: number;
  /** Raw payload — timezone + offset reused on every tick. */
  payload: SystemTime;
};

export function useServerTime() {
  const [time, setTime] = useState<ServerTime | null>(null);
  const [error, setError] = useState<string | null>(null);
  const anchorRef = useRef<Anchor | null>(null);

  useEffect(() => {
    let cancelled = false;

    const render = () => {
      const anchor = anchorRef.current;
      if (!anchor) return;
      const elapsed = performance.now() - anchor.monotonicMs;
      const nowUtcMs = anchor.serverUtcMs + elapsed;
      setTime({
        ...anchor.payload,
        // Advance the displayed utc so consumers that read time.utc also see
        // a moving clock, not a frozen snapshot from the last sync.
        utc: new Date(nowUtcMs).toISOString(),
        serverLocal: utcMsToServerLocal(nowUtcMs, anchor.payload.offsetMinutes),
      });
    };

    const resync = () => {
      systemApi
        .time()
        .then((t) => {
          if (cancelled) return;
          anchorRef.current = {
            serverUtcMs: new Date(t.utc).getTime(),
            monotonicMs: performance.now(),
            payload: t,
          };
          setError(null);
          render();
        })
        .catch((e: unknown) => {
          if (!cancelled) setError(e instanceof Error ? e.message : String(e));
        });
    };

    resync();
    const tickId = window.setInterval(render, TICK_INTERVAL_MS);
    const resyncId = window.setInterval(resync, RESYNC_INTERVAL_MS);

    return () => {
      cancelled = true;
      window.clearInterval(tickId);
      window.clearInterval(resyncId);
    };
  }, []);

  return { time, error };
}

/**
 * Convert a UTC ISO string to server-local display time.
 * Uses the server's offsetMinutes so we never trust the browser's timezone.
 * Returns "yyyy-MM-dd HH:mm" by default, or "yyyy-MM-dd HH:mm:ss" with seconds=true.
 */
export function toServerLocal(iso: string, offsetMinutes: number, seconds = false): string {
  const localMs = new Date(iso).getTime() + offsetMinutes * 60_000;
  const s = new Date(localMs).toISOString().replace("T", " ");
  return seconds ? s.slice(0, 19) : s.slice(0, 16);
}

/**
 * Format the UTC portion for gray subtitle display: "(UTC HH:mm)"
 */
export function formatUtcSuffix(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `(UTC ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())})`;
}
