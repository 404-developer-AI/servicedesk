import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { useRouterState } from "@tanstack/react-router";
import { noteThreadApi, noteThreadKey } from "@/lib/note-threads-api";
import type { TicketTimeline as TicketTimelineState } from "../useTicketTimeline";

export function useTimelineDeepLinks({
  ticketId,
  timeline,
}: {
  ticketId: string;
  timeline: TicketTimelineState;
}) {
  // v0.0.12 stap 4 — deep-link to a specific event (from mention
  // notifications, mail CTAs, etc.). Runs once events are in the DOM.
  // Scroll + ring-animate pattern copied from PinnedEventsSummary.handleJump.
  // v0.1.33 — an event outside the loaded page is loaded first (once per
  // hash); the effect re-runs when it arrives.
  const deepLinkLoadRef = React.useRef<string | null>(null);
  const { events: timelineEvents, ensureLoaded: ensureEventLoaded } = timeline;
  React.useEffect(() => {
    if (!timelineEvents.length) return;
    const hash = window.location.hash;
    const match = hash.match(/^#event-(\d+)$/);
    if (!match) return;
    const eventId = match[1];
    if (!timelineEvents.some((e) => String(e.id) === eventId)) {
      const key = `${ticketId}:${eventId}`;
      if (deepLinkLoadRef.current === key) return;
      deepLinkLoadRef.current = key;
      void ensureEventLoaded(Number(eventId));
      return;
    }
    // requestAnimationFrame waits for the timeline to render before we try
    // to find the anchor — the query may resolve before the DOM settles.
    requestAnimationFrame(() => {
      const el = document.getElementById(`event-${eventId}`);
      if (!el) return;
      el.scrollIntoView({ behavior: "smooth", block: "center" });
      el.classList.add("ring-2", "ring-primary/50", "rounded-lg");
      setTimeout(() => {
        el.classList.remove("ring-2", "ring-primary/50", "rounded-lg");
      }, 2000);
    });
  }, [timelineEvents, ensureEventLoaded, ticketId]);

  // v0.1.54 — deep-link to a reply (#reply-{id}, from a reply/like
  // notification or search): make sure its note is loaded, then scroll to
  // the reply once NoteThread has opened the thread.
  const routeHash = useRouterState({ select: (s) => s.location.hash });
  const threadsForLink = useQuery({
    queryKey: noteThreadKey(ticketId),
    queryFn: () => noteThreadApi.get(ticketId),
    staleTime: 15_000,
    enabled: /^#?reply-\d+$/.test(routeHash ?? ""),
  });
  const replyLinkLoadRef = React.useRef<string | null>(null);
  React.useEffect(() => {
    const m = /^#?reply-(\d+)$/.exec(routeHash ?? "");
    if (!m || !threadsForLink.data || !timelineEvents.length) return;
    const replyId = Number(m[1]);
    const reply = threadsForLink.data.replies.find((r) => r.id === replyId);
    if (!reply) return;
    if (!timelineEvents.some((e) => e.id === reply.rootEventId)) {
      const key = `${ticketId}:reply:${replyId}`;
      if (replyLinkLoadRef.current === key) return;
      replyLinkLoadRef.current = key;
      void ensureEventLoaded(reply.rootEventId);
      return;
    }
    const tryScroll = (attempt: number) => {
      const el = document.getElementById(`reply-${replyId}`);
      if (!el) {
        if (attempt < 10) setTimeout(() => tryScroll(attempt + 1), 100);
        return;
      }
      el.scrollIntoView({ behavior: "smooth", block: "center" });
      el.classList.add("ring-2", "ring-primary/50");
      setTimeout(() => el.classList.remove("ring-2", "ring-primary/50"), 2000);
    };
    requestAnimationFrame(() => tryScroll(0));
  }, [routeHash, threadsForLink.data, timelineEvents, ensureEventLoaded, ticketId]);
}
