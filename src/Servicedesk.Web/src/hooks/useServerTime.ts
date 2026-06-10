import { useSyncExternalStore } from "react";
import { systemApi, type SystemTime } from "@/lib/api";

// How often to re-sync with the server. The visible wall clock ticks locally
// every second between syncs — between syncs we just add elapsed ms to the
// last server snapshot, so the display is still server-anchored without
// hammering /api/system/time at 1Hz per open tab.
//
// One shared fetcher runs for the whole tab regardless of how many
// components call useServerTime(); see the module-level store below.
//
// TODO(settings): move to Ui.ServerTimeSync.IntervalSeconds once there is a
// UI to edit settings (backend store already exists — v0.0.7).
const RESYNC_INTERVAL_MS = 120_000;
const TICK_INTERVAL_MS = 1_000;
// Deferring stop() past Strict-Mode's synchronous mount→cleanup→mount cycle
// avoids tearing down and re-fetching when the only subscriber briefly
// unmounts during dev double-invocation.
const STOP_DEBOUNCE_MS = 250;

export type ServerTime = SystemTime & {
  /** Server local time (as a Date interpreted in UTC fields). */
  serverLocal: Date;
};

type StoreSnapshot = { time: ServerTime | null; error: string | null };
type Anchor = {
  serverUtcMs: number;
  monotonicMs: number;
  payload: SystemTime;
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
  return `${pad(d.getUTCDate())}/${pad(d.getUTCMonth() + 1)}/${d.getUTCFullYear()}`;
}

const EMPTY_SNAPSHOT: StoreSnapshot = { time: null, error: null };

let anchor: Anchor | null = null;
let snapshot: StoreSnapshot = EMPTY_SNAPSHOT;
const listeners = new Set<() => void>();
let tickId: number | null = null;
let resyncId: number | null = null;
let stopTimeoutId: number | null = null;
let inflight: Promise<void> | null = null;

function emit(): void {
  for (const l of listeners) l();
}

function updateFromAnchor(): void {
  if (!anchor) return;
  const elapsed = performance.now() - anchor.monotonicMs;
  const nowUtcMs = anchor.serverUtcMs + elapsed;
  snapshot = {
    time: {
      ...anchor.payload,
      // Advance the displayed utc so consumers that read time.utc also see
      // a moving clock, not a frozen snapshot from the last sync.
      utc: new Date(nowUtcMs).toISOString(),
      serverLocal: utcMsToServerLocal(nowUtcMs, anchor.payload.offsetMinutes),
    },
    error: null,
  };
  emit();
}

function resync(): Promise<void> {
  if (inflight) return inflight;
  inflight = (async () => {
    try {
      const t = await systemApi.time();
      anchor = {
        serverUtcMs: new Date(t.utc).getTime(),
        monotonicMs: performance.now(),
        payload: t,
      };
      updateFromAnchor();
    } catch (e) {
      snapshot = {
        time: snapshot.time,
        error: e instanceof Error ? e.message : String(e),
      };
      emit();
    } finally {
      inflight = null;
    }
  })();
  return inflight;
}

function start(): void {
  if (tickId !== null) return;
  void resync();
  tickId = window.setInterval(updateFromAnchor, TICK_INTERVAL_MS);
  resyncId = window.setInterval(() => void resync(), RESYNC_INTERVAL_MS);
}

function stop(): void {
  if (tickId !== null) {
    window.clearInterval(tickId);
    tickId = null;
  }
  if (resyncId !== null) {
    window.clearInterval(resyncId);
    resyncId = null;
  }
}

function subscribe(listener: () => void): () => void {
  if (stopTimeoutId !== null) {
    window.clearTimeout(stopTimeoutId);
    stopTimeoutId = null;
  }
  listeners.add(listener);
  if (tickId === null) start();
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && stopTimeoutId === null) {
      stopTimeoutId = window.setTimeout(() => {
        stopTimeoutId = null;
        if (listeners.size === 0) stop();
      }, STOP_DEBOUNCE_MS);
    }
  };
}

function getSnapshot(): StoreSnapshot {
  return snapshot;
}

function getServerSnapshot(): StoreSnapshot {
  return EMPTY_SNAPSHOT;
}

export function useServerTime() {
  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}

/**
 * Convert a UTC ISO string to server-local display time.
 * Uses the server's offsetMinutes so we never trust the browser's timezone.
 * Returns "dd/MM/yyyy - HH:mm" by default, or "dd/MM/yyyy - HH:mm:ss" with seconds=true.
 */
export function toServerLocal(iso: string, offsetMinutes: number, seconds = false): string {
  const d = new Date(new Date(iso).getTime() + offsetMinutes * 60_000);
  const pad = (n: number) => String(n).padStart(2, "0");
  const stamp = `${pad(d.getUTCDate())}/${pad(d.getUTCMonth() + 1)}/${d.getUTCFullYear()} - ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
  return seconds ? `${stamp}:${pad(d.getUTCSeconds())}` : stamp;
}
