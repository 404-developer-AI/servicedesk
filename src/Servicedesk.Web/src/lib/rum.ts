// v0.1.24 — Real-user monitoring for the staff app (Settings → Performance →
// Frontend). Started once by the AppShell for signed-in agents/admins; the
// customer portal never loads it.
//
// What it reports (durations only — the server stamps the time):
//   * Core Web Vitals (LCP, INP, CLS, FCP, TTFB) per route template
//   * navigation timing of the first load (DNS, TCP, TLS, TTFB, download)
//   * every /api call: browser total vs server time (Server-Timing) → network
//   * long tasks / long animation frames with the blocking script's chunk
//   * SPA route-change duration, API calls + KB per opened screen
//   * JS heap of long-lived tabs, SignalR reconnects, HTTP protocol
//
// Routes are TanStack route templates ("/tickets/$ticketId"), API paths are
// reduced to templates (ids → {id}); the server scrubs both again. Sampling
// is decided once per page load from /api/perf/config. Batches go out every
// `flushSeconds` and when the tab is hidden, via fetch(keepalive) — not
// sendBeacon, because the double-submit CSRF header has to ride along.

import { onCLS, onFCP, onINP, onLCP, onTTFB, type Metric } from "web-vitals";
import { csrfHeader } from "@/lib/csrf";
import { SERVER_RECONNECTED_EVENT } from "@/lib/appUpdate";

type Item = { m: string; r: string; d?: string; v: number };

type RouterLike = {
  state: { matches: Array<{ routeId: string }> };
  routesById: Record<string, { fullPath?: string } | undefined>;
  subscribe: (event: "onBeforeNavigate" | "onResolved", fn: () => void) => () => void;
};

type PerfConfig = { enabled: boolean; samplePercent: number; diagnose: boolean; flushSeconds: number };

const ENDPOINT = "/api/perf/rum";
const MAX_ITEMS_PER_BATCH = 250;
const MAX_QUEUE = 1500;
const SCREEN_WINDOW_MS = 10_000;

let started = false;
const queue: Item[] = [];
let device = "desktop";
let connection = "unknown";

const GUID = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;

/** "/api/tickets/3f2b…/events?x=1" → "/api/tickets/{id}/events" */
export function apiTemplate(pathname: string): string {
  return pathname
    .split("?")[0]
    .split("/")
    .map((seg) => (GUID.test(seg) || /^\d+$/.test(seg) || seg.length > 40 ? "{id}" : seg))
    .join("/")
    .slice(0, 120);
}

/** "…/assets/TicketDetailPage-Ab12Cd.js" → "TicketDetailPage" */
export function chunkName(url: string | undefined | null): string {
  if (!url) return "";
  try {
    const path = new URL(url, window.location.origin).pathname;
    const file = path.split("/").pop() ?? "";
    return file.replace(/\.(m?js)$/i, "").replace(/-[A-Za-z0-9_]{6,}$/, "").slice(0, 60) || "inline";
  } catch {
    return "";
  }
}

function push(item: Item) {
  if (queue.length >= MAX_QUEUE) return;
  if (!Number.isFinite(item.v) || item.v < 0) return;
  queue.push({ ...item, v: Math.round(item.v * 100) / 100 });
}

function flush() {
  if (queue.length === 0) return;
  const items = queue.splice(0, MAX_ITEMS_PER_BATCH);
  const body = JSON.stringify({ device, conn: connection, items });
  try {
    void fetch(ENDPOINT, {
      method: "POST",
      credentials: "include",
      keepalive: true,
      headers: { "Content-Type": "application/json", ...csrfHeader(ENDPOINT) },
      body,
    }).catch(() => {
      // telemetry is best-effort
    });
  } catch {
    // keepalive quota exceeded or fetch unavailable — drop the batch
  }
  if (queue.length > 0) flush();
}

function detectDevice(): string {
  const coarse = window.matchMedia?.("(pointer: coarse)").matches ?? false;
  const w = window.innerWidth;
  if (coarse && w < 768) return "mobile";
  if (coarse && w < 1200) return "tablet";
  return "desktop";
}

function detectConnection(): string {
  const nav = navigator as Navigator & { connection?: { effectiveType?: string } };
  return nav.connection?.effectiveType ?? "unknown";
}

export async function startRum(router: RouterLike): Promise<void> {
  if (started || typeof window === "undefined" || typeof PerformanceObserver === "undefined") return;
  started = true;

  let config: PerfConfig;
  try {
    const res = await fetch("/api/perf/config", { credentials: "include", headers: { Accept: "application/json" } });
    if (!res.ok) return;
    config = (await res.json()) as PerfConfig;
  } catch {
    return;
  }
  if (!config.enabled || Math.random() * 100 >= config.samplePercent) return;

  device = detectDevice();
  connection = detectConnection();

  const currentRoute = (): string => {
    const matches = router.state.matches;
    const id = matches[matches.length - 1]?.routeId;
    if (!id) return window.location.pathname.slice(0, 120);
    const full = router.routesById[id]?.fullPath ?? id;
    return (full.length > 1 ? full.replace(/\/$/, "") : full) || "/";
  };

  const initialRoute = currentRoute();

  // ---- Web Vitals (reported once per page load, attributed to the landing route)
  const vital = (name: string) => (m: Metric) => push({ m: name, r: initialRoute, v: m.value });
  onLCP(vital("lcp"));
  onINP(vital("inp"));
  onCLS(vital("cls"));
  onFCP(vital("fcp"));
  onTTFB(vital("ttfb"));

  // ---- Navigation timing of the first document load
  const navEntry = performance.getEntriesByType("navigation")[0] as PerformanceNavigationTiming | undefined;
  if (navEntry) {
    const nav = (m: string, v: number) => {
      if (v >= 0) push({ m, r: initialRoute, v });
    };
    nav("nav_dns", navEntry.domainLookupEnd - navEntry.domainLookupStart);
    const tls = navEntry.secureConnectionStart > 0 ? navEntry.connectEnd - navEntry.secureConnectionStart : 0;
    nav("nav_tcp", navEntry.connectEnd - navEntry.connectStart - tls);
    nav("nav_tls", tls);
    nav("nav_ttfb", navEntry.responseStart - navEntry.requestStart);
    nav("nav_download", navEntry.responseEnd - navEntry.responseStart);
    nav("nav_dom", navEntry.domInteractive);
    window.addEventListener("load", () => {
      setTimeout(() => nav("nav_load", navEntry.loadEventEnd || performance.now()), 0);
    }, { once: true });
  }

  // ---- Screens: views, route-change duration, API calls/KB per screen
  let screenRoute = initialRoute;
  let screenCalls = 0;
  let screenBytes = 0;
  let screenOpen = true;
  let screenTimer: number | undefined;
  let navStart = 0;

  const closeScreen = () => {
    if (!screenOpen) return;
    screenOpen = false;
    window.clearTimeout(screenTimer);
    push({ m: "screen_api_calls", r: screenRoute, v: screenCalls });
    push({ m: "screen_api_kb", r: screenRoute, v: screenBytes / 1024 });
  };
  const openScreen = (route: string) => {
    screenRoute = route;
    screenCalls = 0;
    screenBytes = 0;
    screenOpen = true;
    push({ m: "view", r: route, v: 1 });
    screenTimer = window.setTimeout(closeScreen, SCREEN_WINDOW_MS);
  };
  openScreen(initialRoute);

  router.subscribe("onBeforeNavigate", () => {
    navStart = performance.now();
    closeScreen();
  });
  router.subscribe("onResolved", () => {
    const route = currentRoute();
    const begin = navStart;
    // Two frames: the new page has been committed and painted.
    requestAnimationFrame(() =>
      requestAnimationFrame(() => {
        if (begin > 0) push({ m: "route_change", r: route, v: performance.now() - begin });
      }),
    );
    openScreen(route);
  });

  // ---- API calls: browser total vs server part
  let protocolSamples = 0;
  try {
    const resources = new PerformanceObserver((list) => {
      for (const raw of list.getEntries()) {
        const e = raw as PerformanceResourceTiming;
        if (e.initiatorType !== "fetch" && e.initiatorType !== "xmlhttprequest") continue;
        let url: URL;
        try {
          url = new URL(e.name);
        } catch {
          continue;
        }
        if (url.origin !== window.location.origin || !url.pathname.startsWith("/api/")) continue;
        if (url.pathname.startsWith("/api/perf/") || url.pathname.startsWith("/api/admin/performance")) continue;

        const route = currentRoute();
        if (screenOpen) {
          screenCalls += 1;
          screenBytes += e.encodedBodySize || e.transferSize || 0;
        }
        const total = e.responseEnd - e.startTime;
        const detail = apiTemplate(url.pathname);
        push({ m: "api_total", r: route, d: detail, v: total });
        const server = e.serverTiming?.find((s) => s.name === "total")?.duration;
        if (typeof server === "number" && server >= 0) {
          push({ m: "api_server", r: route, d: detail, v: server });
          push({ m: "api_network", r: route, d: detail, v: Math.max(0, total - server) });
        }
        if (e.nextHopProtocol && protocolSamples++ % 20 === 0) {
          push({ m: "protocol", r: route, d: e.nextHopProtocol, v: 1 });
        }
      }
    });
    resources.observe({ type: "resource", buffered: true });
  } catch {
    // Resource Timing unavailable
  }

  // ---- Main-thread blocking: long tasks + long animation frames (Chromium)
  try {
    const longTasks = new PerformanceObserver((list) => {
      for (const e of list.getEntries()) push({ m: "long_task", r: currentRoute(), v: e.duration });
    });
    longTasks.observe({ type: "longtask", buffered: true });
  } catch {
    // unsupported
  }
  try {
    type LoafScript = { duration: number; sourceURL?: string; sourceFunctionName?: string };
    type Loaf = PerformanceEntry & { scripts?: LoafScript[] };
    const loaf = new PerformanceObserver((list) => {
      for (const raw of list.getEntries()) {
        const e = raw as Loaf;
        const top = [...(e.scripts ?? [])].sort((a, b) => b.duration - a.duration)[0];
        if (!top || top.duration < 30) continue;
        const chunk = chunkName(top.sourceURL);
        const fn = (top.sourceFunctionName ?? "").replace(/[^A-Za-z0-9_$]/g, "").slice(0, 30);
        const detail = (fn ? `${fn}@${chunk}` : chunk).slice(0, 80);
        if (detail) push({ m: "loaf", r: currentRoute(), d: detail, v: top.duration });
      }
    });
    loaf.observe({ type: "long-animation-frame", buffered: true });
  } catch {
    // unsupported outside Chromium
  }

  // ---- Long-lived tabs: JS heap trend (Chromium only)
  const memory = (performance as Performance & { memory?: { usedJSHeapSize: number } }).memory;
  if (memory) {
    window.setInterval(() => push({ m: "js_heap_mb", r: "*", v: memory.usedJSHeapSize / 1024 / 1024 }), 60_000);
  }

  // ---- Realtime: SignalR reconnects (presence hub raises this event)
  window.addEventListener(SERVER_RECONNECTED_EVENT, () => push({ m: "signalr_reconnect", r: currentRoute(), v: 1 }));

  // ---- Delivery
  window.setInterval(flush, Math.max(10, config.flushSeconds) * 1000);
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") {
      closeScreen();
      flush();
    }
  });
}
