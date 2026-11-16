import * as React from "react";
import { useQuery, useQueryClient, useMutation } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Bell, X, AtSign, CheckCheck, ExternalLink, ListChecks, MessageSquareReply, ThumbsUp } from "lucide-react";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import { notificationApi, notificationHash, notificationHeadline, type UserNotification } from "@/lib/notification-api";
import { useServerOffsetMinutes, toServerLocal } from "@/hooks/useServerTime";
import { PulseDot } from "@/shell/PulseDot";
import { TaggedNoteHoverCard } from "@/components/TaggedNoteHoverCard";
import { isTagPreviewable, keepOpenForTagPreview } from "@/lib/tagPreview";

type Props = {
  collapsed: boolean;
  /// Wrapper classes; defaults to the stand-alone "mx-3 mb-2" block.
  className?: string;
};

/// v0.0.12 stap 4 — persistent sidebar widget showing pending @@-mentions.
/// Sits above the sidebar collapse-button. Click-through marks viewed +
/// navigates to the exact event; X-button acks without navigating; the
/// footer lets the agent ack-all and jump to full history.
export function NotificationsWidget({ collapsed, className }: Props) {
  const listRef = React.useRef<HTMLDivElement | null>(null);
  const { data, isLoading } = useQuery({
    queryKey: ["notifications", "pending"],
    queryFn: () => notificationApi.listPending(),
    // Real-time pushes drive refetches via the SignalR hook, so we don't
    // need a polling interval. Refetch-on-focus catches the case where
    // the tab was backgrounded during a push.
    refetchOnWindowFocus: true,
    staleTime: 30_000,
  });
  const items = data ?? [];
  const pendingCount = items.length;
  const hasPending = pendingCount > 0;

  // Both layouts use a single clickable trigger that opens a popover
  // containing the scrollable list + ack-all/all-tags footer. The only
  // difference between expanded and collapsed is the trigger chrome:
  // a label+count pill in the expanded sidebar, an icon-only tile when
  // the sidebar is collapsed.
  return (
    <div className={cn(className ?? "mx-3 mb-2", collapsed && "flex justify-center")}>
      <Popover>
        <PopoverTrigger asChild>
          {collapsed ? (
            <button
              type="button"
              title={hasPending ? `${pendingCount} pending tag${pendingCount === 1 ? "" : "s"}` : "No pending tags"}
              className={cn(
                "relative flex h-9 w-9 items-center justify-center rounded-lg border border-glass bg-glass text-muted-foreground transition-colors",
                "hover:bg-glass-hover hover:text-foreground",
                hasPending && "text-purple-200",
              )}
            >
              <Bell className="h-4 w-4" />
              {hasPending ? <PulseDot count={pendingCount} /> : null}
            </button>
          ) : (
            <button
              type="button"
              title={hasPending ? `${pendingCount} pending tag${pendingCount === 1 ? "" : "s"}` : "No pending tags"}
              className={cn(
                "flex h-8 w-full min-w-0 items-center gap-1 rounded-lg border border-glass bg-glass px-2 text-left transition-colors",
                "hover:bg-glass-hover",
              )}
            >
              <div className="relative shrink-0">
                <Bell className={cn("h-3.5 w-3.5", hasPending ? "text-purple-200" : "text-muted-foreground")} />
                {hasPending ? <PulseDot count={null} /> : null}
              </div>
              <span className="truncate text-[10px] font-medium uppercase tracking-[0.06em] text-muted-foreground">
                Tags
              </span>
              {hasPending ? (
                <span className="ml-auto shrink-0 rounded-full border border-purple-500/30 bg-purple-500/20 px-1.5 py-px text-[10px] font-medium text-purple-200">
                  {pendingCount}
                </span>
              ) : null}
            </button>
          )}
        </PopoverTrigger>
        <PopoverContent ref={listRef} side="right" align="end" className="w-88 p-0" onInteractOutside={keepOpenForTagPreview}>
          <NotificationPanel items={items} loading={isLoading} listRef={listRef} />
        </PopoverContent>
      </Popover>
    </div>
  );
}

function NotificationPanel({
  items,
  loading,
  listRef,
}: {
  items: UserNotification[];
  loading: boolean;
  /// The popover; the note preview lines its bottom edge up with it.
  listRef: React.RefObject<HTMLDivElement | null>;
}) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const serverOffsetMinutes = useServerOffsetMinutes();
  const offsetMinutes = serverOffsetMinutes ?? 0;

  const markViewed = useMutation({
    mutationFn: (id: string) => notificationApi.markViewed(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["notifications", "pending"] });
    },
  });
  const markAcked = useMutation({
    mutationFn: (id: string) => notificationApi.markAcked(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["notifications", "pending"] });
    },
  });
  const ackAll = useMutation({
    mutationFn: () => notificationApi.ackAll(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["notifications", "pending"] });
    },
  });

  const handleJump = (n: UserNotification) => {
    // Fire the mark-viewed in the background — navigation shouldn't wait
    // on the POST. The server also stamps viewed_utc + acked_utc in one
    // idempotent UPDATE, so re-clicks are safe.
    markViewed.mutate(n.id);
    void navigate({
      to: "/tickets/$ticketId",
      params: { ticketId: n.ticketId },
      hash: notificationHash(n),
    });
  };

  if (items.length === 0) {
    return (
      <div className="px-3 py-6 text-center text-xs text-muted-foreground/70">
        {loading ? "Loading…" : "No pending tags"}
      </div>
    );
  }

  return (
    <div className="flex flex-col">
      <div className="max-h-[320px] overflow-y-auto divide-y divide-glass">
        {items.map((n) => {
          const localPart = n.sourceUserEmail?.split("@")[0] ?? "agent";
          const when = toServerLocal(n.createdUtc, offsetMinutes);
          const isChecklistBlock = n.eventType === "ChecklistCloseBlocked";
          const isTriggerNotice = n.eventType === "TriggerNotice";
          const isReply = n.notificationType === "note_reply";
          const isLike = n.notificationType === "note_like";
          // The ticket subject: the note itself is in the hover preview.
          // Checklist / trigger notices keep their message here.
          const subtitle = isTagPreviewable(n.eventType) ? n.ticketSubject : n.previewText || n.ticketSubject;
          return (
            // The whole row (incl. the dismiss button) is the hover anchor,
            // so the preview opens beside the list, never over the X.
            <TaggedNoteHoverCard key={n.id} ticketId={n.ticketId} eventId={n.eventId} eventType={n.eventType}
              fallbackText={n.previewText} alignBottomTo={listRef} dimBackground>
            <div className="group flex items-start gap-2 px-3 py-2 transition-colors hover:bg-glass-hover">
              <button
                type="button"
                onClick={() => handleJump(n)}
                className="flex min-w-0 flex-1 items-start gap-2 text-left"
              >
                {isChecklistBlock ? (
                  <ListChecks className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-300" />
                ) : isTriggerNotice ? (
                  <Bell className="mt-0.5 h-3.5 w-3.5 shrink-0 text-primary" />
                ) : isReply ? (
                  <MessageSquareReply className="mt-0.5 h-3.5 w-3.5 shrink-0 text-primary" />
                ) : isLike ? (
                  <ThumbsUp className="mt-0.5 h-3.5 w-3.5 shrink-0 text-primary" />
                ) : (
                  <AtSign className="mt-0.5 h-3.5 w-3.5 shrink-0 text-purple-300" />
                )}
                <div className="min-w-0 flex-auto">
                  <div className="flex items-center gap-1 text-xs">
                    {isChecklistBlock ? (
                      <span className="font-medium text-amber-200">Checklist blocked a trigger</span>
                    ) : isTriggerNotice ? (
                      <span className="font-medium text-foreground">Notice</span>
                    ) : isReply || isLike ? (
                      <span className="font-medium text-foreground">{notificationHeadline(n)}</span>
                    ) : (
                      <>
                        <span className="font-medium text-foreground">@{localPart}</span>
                        <span className="text-muted-foreground">in</span>
                      </>
                    )}
                    <span className="font-mono text-[11px] text-muted-foreground">#{n.ticketNumber}</span>
                  </div>
                  {subtitle && (
                    <div className="mt-0.5 truncate text-[11px] text-muted-foreground/90">{subtitle}</div>
                  )}
                  <div className="mt-0.5 text-[10px] text-muted-foreground/60">
                    {when}
                  </div>
                </div>
              </button>
              <button
                type="button"
                onClick={() => markAcked.mutate(n.id)}
                disabled={markAcked.isPending}
                title="Dismiss without opening"
                className="shrink-0 rounded-md p-1 text-muted-foreground/60 transition-colors hover:bg-glass-hover hover:text-foreground opacity-0 group-hover:opacity-100"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </div>
            </TaggedNoteHoverCard>
          );
        })}
      </div>
      <div className="flex items-center justify-between border-t border-glass px-3 py-2 text-[11px]">
        <button
          type="button"
          onClick={() => ackAll.mutate()}
          disabled={ackAll.isPending || items.length === 0}
          className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-muted-foreground/80 transition-colors hover:bg-glass-hover hover:text-foreground disabled:opacity-40"
        >
          <CheckCheck className="h-3.5 w-3.5" /> Ack all
        </button>
        <button
          type="button"
          onClick={() => navigate({ to: "/profile/mentions" })}
          className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-muted-foreground/80 transition-colors hover:bg-glass-hover hover:text-foreground"
        >
          All tags <ExternalLink className="h-3 w-3" />
        </button>
      </div>
    </div>
  );
}
