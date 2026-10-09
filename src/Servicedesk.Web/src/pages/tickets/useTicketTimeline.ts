import * as React from "react";
import { ticketApi, type TicketDetail, type TicketEvent } from "@/lib/ticket-api";

/// v0.1.33 — paged ticket timeline. `GET /api/tickets/{id}` carries the latest
/// page (`detail.events`); older events are fetched on demand and kept here,
/// per ticket, outside the `["ticket", id]` query so a realtime refetch of the
/// detail doesn't drop them.
///
/// Every time the detail page changes (new event, edit, pin, …) the loaded
/// older range is re-read in one request — from the page's oldest event back
/// to the oldest loaded one — which fills the gap a newer page leaves (events
/// that slid out of the page) and refreshes edits to older events.
type OlderState = {
  ticketId: string;
  events: TicketEvent[];
  hasMore: boolean;
};

export type TicketTimeline = {
  /// Every loaded event, oldest first (older pages + the detail's page).
  events: TicketEvent[];
  hasOlder: boolean;
  loadingOlder: boolean;
  loadOlder: () => Promise<void>;
  loadAll: () => Promise<void>;
  /// Loads back to `eventId` when it isn't loaded yet. Resolves true when the
  /// event is (now) loaded.
  ensureLoaded: (eventId: number) => Promise<boolean>;
};

// Server order is (created_utc, id); merged-in events keep their original
// created_utc, so id alone is not chronological.
function byTimeline(a: TicketEvent, b: TicketEvent): number {
  const ta = Date.parse(a.createdUtc);
  const tb = Date.parse(b.createdUtc);
  if (ta !== tb) return ta - tb;
  return a.id - b.id;
}

export function useTicketTimeline(ticketId: string, detail: TicketDetail | undefined): TicketTimeline {
  const [older, setOlder] = React.useState<OlderState | null>(null);
  const [loadingOlder, setLoadingOlder] = React.useState(false);
  const page = detail?.events;

  // Only the current ticket's older events count (state from a previous
  // ticket is ignored until it is replaced).
  const current = older?.ticketId === ticketId ? older : null;
  const olderRef = React.useRef(current);
  olderRef.current = current;

  const events = React.useMemo(() => {
    const list = page ?? [];
    if (!current || current.events.length === 0) return list;
    const pageIds = new Set(list.map((e) => e.id));
    return [...current.events.filter((e) => !pageIds.has(e.id)), ...list].sort(byTimeline);
  }, [current, page]);

  const hasOlder = current ? current.hasMore : !!detail?.hasOlderEvents;
  const oldestId = events[0]?.id;

  const fetchOlder = React.useCallback(
    async (opts: { untilId?: number; all?: boolean }) => {
      if (oldestId === undefined) return;
      setLoadingOlder(true);
      try {
        const res = await ticketApi.listOlderEvents(ticketId, { beforeId: oldestId, ...opts });
        const prev = olderRef.current?.events ?? [];
        setOlder({ ticketId, events: [...res.events, ...prev], hasMore: res.hasMore });
      } finally {
        setLoadingOlder(false);
      }
    },
    [ticketId, oldestId],
  );

  // Re-read the loaded older range whenever a new detail page arrives.
  const pageOldestId = page?.[0]?.id;
  React.useEffect(() => {
    const loaded = olderRef.current;
    if (!loaded || loaded.events.length === 0 || pageOldestId === undefined) return;
    const untilId = loaded.events[0].id;
    let cancelled = false;
    ticketApi
      .listOlderEvents(ticketId, { beforeId: pageOldestId, untilId })
      .then((res) => {
        if (cancelled || res.events.length === 0) return;
        // A "Load older" that finished meanwhile extended the range — keep it.
        if (olderRef.current?.events[0]?.id !== untilId) return;
        setOlder({ ticketId, events: res.events, hasMore: res.hasMore });
      })
      .catch(() => {
        /* keep what we have; the next page refresh retries */
      });
    return () => {
      cancelled = true;
    };
    // `page` identity changes on every detail refetch — that is the trigger.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ticketId, page]);

  const loadOlder = React.useCallback(() => fetchOlder({}), [fetchOlder]);
  const loadAll = React.useCallback(() => fetchOlder({ all: true }), [fetchOlder]);

  const eventsRef = React.useRef(events);
  eventsRef.current = events;
  const hasOlderRef = React.useRef(hasOlder);
  hasOlderRef.current = hasOlder;
  const ensureLoaded = React.useCallback(
    async (eventId: number) => {
      if (eventsRef.current.some((e) => e.id === eventId)) return true;
      if (!hasOlderRef.current) return false;
      try {
        await fetchOlder({ untilId: eventId });
      } catch {
        return false;
      }
      return true;
    },
    [fetchOlder],
  );

  return { events, hasOlder, loadingOlder, loadOlder, loadAll, ensureLoaded };
}
