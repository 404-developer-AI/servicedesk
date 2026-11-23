import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { useRouterState } from "@tanstack/react-router";
import { groupThreads, noteThreadApi, noteThreadKey } from "@/lib/note-threads-api";
import type { TicketEvent } from "@/lib/ticket-api";
import { useAuth } from "@/auth/authStore";
import { feedbackApi } from "@/lib/feedback-api";
import {
  AttachmentPreviewDialog,
  type AttachmentPreview,
} from "@/components/attachments/AttachmentPreviewDialog";
import { PreviewContext, type PreviewContextValue } from "./timelinePreviewContext";
import { TimelineEvent } from "./timeline/TimelineEvent";

type TicketTimelineProps = {
  ticketId: string;
  ticketNumber: number;
  events: TicketEvent[];
  pinnedEventIds: Set<number>;
};

/// Image/attachment preview context for timeline bodies rendered outside
/// <TicketTimeline> (the mobile ticket page): clicking an inline image or an
/// attachment chip opens the same preview dialog.
export function TimelinePreviewProvider({ children }: { children: React.ReactNode }) {
  const [preview, setPreview] = React.useState<AttachmentPreview | null>(null);
  const previewApi = React.useMemo<PreviewContextValue>(() => ({ open: setPreview }), []);
  return (
    <PreviewContext.Provider value={previewApi}>
      {children}
      <AttachmentPreviewDialog
        preview={preview}
        onOpenChange={(open) => {
          if (!open) setPreview(null);
        }}
      />
    </PreviewContext.Provider>
  );
}

export function TicketTimeline({ ticketId, ticketNumber, events, pinnedEventIds }: TicketTimelineProps) {
  const [preview, setPreview] = React.useState<AttachmentPreview | null>(null);
  const previewApi = React.useMemo<PreviewContextValue>(
    () => ({ open: setPreview }),
    [],
  );

  // For users with either feedback flag: which timeline events already have
  // feedback logged (so the per-item "Log feedback" button can be marked).
  // Restricted users only see their own loggings here (scoped server-side).
  const { user } = useAuth();
  const feedbackEnabled = (user?.feedbackEnabled || user?.feedbackOwnOnly) ?? false;
  const loggedEventsQuery = useQuery({
    queryKey: ["feedback", "logged-events", ticketId],
    queryFn: () => feedbackApi.listLoggedEvents(ticketId),
    enabled: feedbackEnabled,
    staleTime: 30_000,
  });
  // v0.1.54 — replies + likes of every internal note on this ticket in one
  // call; the TicketUpdated push invalidates ["ticket", id] and so this too.
  const threadsQuery = useQuery({
    queryKey: noteThreadKey(ticketId),
    queryFn: () => noteThreadApi.get(ticketId),
    staleTime: 15_000,
  });
  const threads = React.useMemo(() => groupThreads(threadsQuery.data), [threadsQuery.data]);
  const hash = useRouterState({ select: (s) => s.location.hash });
  const hashReplyId = React.useMemo(() => {
    const m = /^#?reply-(\d+)$/.exec(hash ?? "");
    return m ? Number(m[1]) : null;
  }, [hash]);

  const loggedByEvent = React.useMemo(() => {
    const map = new Map<number, { count: number; loggers: string[] }>();
    for (const it of loggedEventsQuery.data?.items ?? []) {
      map.set(it.eventId, { count: it.count, loggers: it.loggers });
    }
    return map;
  }, [loggedEventsQuery.data]);

  if (events.length === 0) {
    return (
      <div className="text-sm text-muted-foreground py-4">
        No events yet.
      </div>
    );
  }

  return (
    <PreviewContext.Provider value={previewApi}>
      <div className="relative border-l-2 border-glass space-y-4 ml-[9px]">
        {events.map((event) => (
          <TimelineEvent
            key={event.id}
            event={event}
            ticketId={ticketId}
            ticketNumber={ticketNumber}
            isPinned={pinnedEventIds.has(event.id)}
            logged={loggedByEvent.get(event.id)}
            thread={threads.get(event.id)}
            forceOpenReplyId={hashReplyId}
          />
        ))}
      </div>
      <AttachmentPreviewDialog
        preview={preview}
        onOpenChange={(open) => {
          if (!open) setPreview(null);
        }}
      />
    </PreviewContext.Provider>
  );
}
