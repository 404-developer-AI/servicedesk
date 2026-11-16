import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { HoverCard, HoverCardContent, HoverCardTrigger } from "@/components/ui/hover-card";
import { Skeleton } from "@/components/ui/skeleton";
import { NoteBodyHtml } from "@/components/NoteBodyHtml";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { ticketApi } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";
import { isTagPreviewable, setTagPreviewOpen } from "@/lib/tagPreview";

// Same hover intent as ContactHoverCard: sweeping the cursor down a list
// doesn't flash cards (or fire requests), a deliberate hover feels instant.
const OPEN_DELAY_MS = 350;
const CLOSE_DELAY_MS = 150;
// Gap between the tag list and the card, and the viewport margin Radix keeps.
const SIDE_OFFSET_PX = 8;
const COLLISION_PADDING_PX = 16;

const KIND_LABEL: Record<string, string> = {
  Note: "Internal note",
  Comment: "Reply",
  Call: "Phone call",
  MailSent: "Outbound mail",
};

/// Wraps a tag row (sidebar TAGS / GROUP TAGS popovers, the history pages)
/// with a hover card showing the full note the tag sits in, rendered by the
/// same NoteBodyHtml as the ticket timeline. The body is fetched only when
/// the card opens, behind queue access server-side; when the agent can't
/// open the ticket any more the stored plain-text preview is shown instead.
///
/// `alignBottomTo` (the sidebar popovers): the card's bottom edge lines up
/// with that element's bottom, so list and preview read as one unit.
/// `dimBackground`: blur the content area while the card is open (the
/// scrim in AppShell, driven by `setTagPreviewOpen`).
export function TaggedNoteHoverCard({
  ticketId,
  eventId,
  eventType,
  fallbackText,
  hint,
  alignBottomTo,
  dimBackground = false,
  children,
}: {
  ticketId: string;
  eventId: number;
  eventType: string | null | undefined;
  fallbackText: string | null | undefined;
  /// What clicking the row does, shown as a footer in the card. Rows put it
  /// here instead of a native `title`, whose tooltip would cover the note.
  hint?: string;
  alignBottomTo?: React.RefObject<HTMLElement | null>;
  dimBackground?: boolean;
  children: React.ReactElement;
}) {
  const id = React.useId();
  // Radix types the trigger as an anchor; with asChild it is the row.
  const triggerRef = React.useRef<HTMLAnchorElement | null>(null);
  const [alignOffset, setAlignOffset] = React.useState(0);

  // Never leave the page blurred when the row unmounts while open.
  React.useEffect(() => () => setTagPreviewOpen(id, false), [id]);

  const onOpenChange = React.useCallback(
    (open: boolean) => {
      if (open && alignBottomTo?.current && triggerRef.current) {
        // align="end" pins the card's bottom to the row's bottom; shift it
        // down by the distance to the list's bottom edge.
        const list = alignBottomTo.current.getBoundingClientRect();
        const row = triggerRef.current.getBoundingClientRect();
        setAlignOffset(-Math.max(0, list.bottom - row.bottom));
      }
      if (dimBackground) setTagPreviewOpen(id, open);
    },
    [alignBottomTo, dimBackground, id],
  );

  if (!isTagPreviewable(eventType)) return children;
  return (
    <HoverCard openDelay={OPEN_DELAY_MS} closeDelay={CLOSE_DELAY_MS} onOpenChange={onOpenChange}>
      {/* Hover only: the tag popovers focus their first row on open, and
          Radix would open the card on that focus too. A prevented focus
          event skips Radix's open handler; the row itself still focuses. */}
      <HoverCardTrigger asChild onFocus={(e) => e.preventDefault()} ref={triggerRef}>
        {children}
      </HoverCardTrigger>
      <HoverCardContent
        side="right"
        align={alignBottomTo ? "end" : "start"}
        alignOffset={alignBottomTo ? alignOffset : 0}
        sideOffset={SIDE_OFFSET_PX}
        collisionPadding={COLLISION_PADDING_PX}
        className="sd-tag-note-preview relative flex w-[28rem] max-w-[90vw] flex-col overflow-hidden p-0"
        style={{ maxHeight: "var(--radix-hover-card-content-available-height)" }}
        // Never steal focus or close the tag popover underneath; a click
        // inside the card must not trigger the row's navigation.
        onMouseDown={(e) => e.preventDefault()}
        onClick={(e) => e.stopPropagation()}
        data-tag-note-preview=""
      >
        <TaggedNoteBody ticketId={ticketId} eventId={eventId} fallbackText={fallbackText} />
        {hint && (
          <div
            className="shrink-0 border-t border-border px-4 py-2 pl-[19px] text-[11px] text-muted-foreground"
            data-testid="tag-note-hint"
          >
            {hint}
          </div>
        )}
      </HoverCardContent>
    </HoverCard>
  );
}

/// Separate component so the request only runs once the card opens (Radix
/// mounts content lazily).
function TaggedNoteBody({
  ticketId,
  eventId,
  fallbackText,
}: {
  ticketId: string;
  eventId: number;
  fallbackText: string | null | undefined;
}) {
  const zone = useServerTimeZone();
  const { data, isLoading, isError } = useQuery({
    queryKey: ["event-preview", ticketId, eventId],
    queryFn: () => ticketApi.getEventPreview(ticketId, eventId),
    staleTime: 300_000,
    retry: false,
  });

  if (isLoading) {
    return (
      <div className="space-y-2 p-4" data-testid="tag-note-loading">
        <Skeleton className="h-3 w-40" />
        <Skeleton className="h-3 w-full" />
        <Skeleton className="h-3 w-5/6" />
        <Skeleton className="h-3 w-2/3" />
      </div>
    );
  }

  if (isError || !data) {
    return (
      <div className="space-y-2 p-4 text-sm">
        {fallbackText && <p className="whitespace-pre-wrap break-words">{fallbackText}</p>}
        <p className="text-xs text-muted-foreground">
          Full note not available — you may no longer have access to this ticket.
        </p>
      </div>
    );
  }

  const internal = data.isInternal;
  return (
    <>
      {internal && <div className="sd-tag-note-accent absolute inset-y-0 left-0 w-[3px] bg-amber-500" aria-hidden />}
      <div className="shrink-0 border-b border-border px-4 pt-2.5 pb-2 pl-[19px]">
        <div className="flex min-w-0 items-baseline gap-1.5 text-[13px] font-semibold text-foreground">
          <span className="shrink-0 font-mono text-[11px] font-medium text-muted-foreground">#{data.ticketNumber}</span>
          <span className="truncate">{data.ticketSubject}</span>
        </div>
        <div className="mt-1 flex min-w-0 items-center gap-1.5 text-[11px] text-muted-foreground">
          <span
            className={cn(
              "shrink-0 rounded border px-1.5 text-[10px] font-medium",
              internal
                ? "border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-400/30 dark:bg-amber-400/10 dark:text-amber-200"
                : "border-border bg-muted text-muted-foreground",
            )}
          >
            {KIND_LABEL[data.eventType] ?? "Note"}
          </span>
          {data.authorName && <span className="truncate">{data.authorName}</span>}
          <span aria-hidden>·</span>
          <span className="shrink-0 tabular-nums">
            {formatDateTime(data.createdUtc, zone)}
            {data.editedUtc && " (edited)"}
          </span>
        </div>
      </div>
      <ScrollBody>
        {data.bodyHtml.trim() ? (
          <NoteBodyHtml html={data.bodyHtml} />
        ) : (
          <p className="text-sm text-muted-foreground">{fallbackText || "This note has no text."}</p>
        )}
      </ScrollBody>
    </>
  );
}

/// The note body: max ~420px, scrolls inside the card, with a fade at the
/// bottom while there is more to read.
function ScrollBody({ children }: { children: React.ReactNode }) {
  const ref = React.useRef<HTMLDivElement | null>(null);
  const [more, setMore] = React.useState(false);
  const measure = React.useCallback(() => {
    const el = ref.current;
    if (el) setMore(el.scrollTop + el.clientHeight < el.scrollHeight - 2);
  }, []);
  React.useLayoutEffect(() => {
    measure();
    const el = ref.current;
    if (!el || typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [measure]);
  return (
    <div className="relative flex min-h-0 flex-col">
      <div
        ref={ref}
        onScroll={measure}
        className="min-h-0 max-h-[420px] overflow-y-auto px-4 py-3 pl-[19px]"
        data-testid="tag-note-body"
      >
        {children}
      </div>
      {more && <div className="sd-tag-note-fade pointer-events-none absolute inset-x-0 bottom-0 h-8" aria-hidden />}
    </div>
  );
}
