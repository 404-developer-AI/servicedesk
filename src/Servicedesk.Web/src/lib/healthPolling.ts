/// v0.0.99 — every health poller (dashboard pill, critical banner, health
/// tiles, Health page) follows the cadence the server sends back inside the
/// health payload (`Health.PollIntervalSeconds`), so admins tune one setting
/// and every open tab converges on the next poll. Before this each component
/// hardcoded 30 s; with many tabs those synchronized polls were the main
/// source of connection-pool bursts on the server.
///
/// Usage: `refetchInterval: (q) => pollIntervalMs(q.state.data)`.
export const HEALTH_POLL_FALLBACK_MS = 30_000;
const MIN_MS = 5_000;
const MAX_MS = 600_000;

export function pollIntervalMs(data: { pollIntervalSeconds?: number } | undefined): number {
  const seconds = data?.pollIntervalSeconds;
  if (typeof seconds !== "number" || !Number.isFinite(seconds) || seconds <= 0) {
    return HEALTH_POLL_FALLBACK_MS;
  }
  return Math.min(MAX_MS, Math.max(MIN_MS, Math.round(seconds * 1000)));
}
