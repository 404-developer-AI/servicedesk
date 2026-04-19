import { useQuery, useQueryClient, useMutation } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { motion } from "framer-motion";
import { Bell, X, AtSign, CheckCheck, ExternalLink } from "lucide-react";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import { notificationApi, type UserNotification } from "@/lib/notification-api";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";

type Props = {
  collapsed: boolean;
};

/// v0.0.12 stap 4 — persistent sidebar widget showing pending @@-mentions.
/// Sits above the sidebar collapse-button. Click-through marks viewed +
/// navigates to the exact event; X-button acks without navigating; the
/// footer lets the agent ack-all and jump to full history.
export function NotificationsWidget({ collapsed }: Props) {
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
    <div className="mx-3 mb-2">
      <Popover>
        <PopoverTrigger asChild>
          {collapsed ? (
            <button
              type="button"
              title={hasPending ? `${pendingCount} pending tag${pendingCount === 1 ? "" : "s"}` : "No pending tags"}
              className={cn(
                "relative flex h-9 w-9 items-center justify-center rounded-lg border border-white/10 bg-white/[0.03] text-muted-foreground transition-colors",
                "hover:bg-white/[0.06] hover:text-foreground",
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
                "flex w-full items-center gap-2 rounded-[var(--radius)] border border-white/10 bg-white/[0.03] px-3 py-2 text-left transition-colors",
                "hover:bg-white/[0.06]",
              )}
            >
              <div className="relative">
                <Bell className={cn("h-4 w-4", hasPending ? "text-purple-200" : "text-muted-foreground")} />
                {hasPending ? <PulseDot count={null} /> : null}
              </div>
              <span className="text-xs font-medium uppercase tracking-[0.12em] text-muted-foreground">
                Tags
              </span>
              {hasPending ? (
                <span className="ml-auto rounded-full border border-purple-500/30 bg-purple-500/20 px-2 py-0.5 text-[10px] font-medium text-purple-200">
                  {pendingCount}
                </span>
              ) : null}
            </button>
          )}
        </PopoverTrigger>
        <PopoverContent side="right" align="end" className="w-[22rem] p-0">
          <NotificationPanel items={items} loading={isLoading} />
        </PopoverContent>
      </Popover>
    </div>
  );
}

function PulseDot({ count }: { count: number | null }) {
  // Small red-dot badge with a slow pulse. `count` null → pure indicator
  // (used when an adjacent count badge already shows the number).
  return (
    <motion.span
      animate={{ opacity: [1, 0.35, 1] }}
      transition={{ duration: 2, repeat: Infinity, ease: "easeInOut" }}
      className="absolute -top-0.5 -right-0.5 flex h-2.5 min-w-2.5 items-center justify-center rounded-full bg-red-500 text-[9px] font-bold leading-none text-white shadow-[0_0_0_2px_hsl(var(--background))]"
      aria-hidden={count === null ? true : undefined}
    >
      {count !== null && count > 0 && count < 10 ? count : ""}
    </motion.span>
  );
}

function NotificationPanel({
  items,
  loading,
}: {
  items: UserNotification[];
  loading: boolean;
}) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const serverTime = useServerTime();
  const offsetMinutes = serverTime.time?.offsetMinutes ?? 0;

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
      hash: `event-${n.eventId}`,
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
      <div className="max-h-[320px] overflow-y-auto divide-y divide-white/5">
        {items.map((n) => {
          const localPart = n.sourceUserEmail?.split("@")[0] ?? "agent";
          const when = toServerLocal(n.createdUtc, offsetMinutes);
          return (
            <div
              key={n.id}
              className="group flex items-start gap-2 px-3 py-2 transition-colors hover:bg-white/[0.04]"
            >
              <button
                type="button"
                onClick={() => handleJump(n)}
                className="flex min-w-0 flex-1 items-start gap-2 text-left"
              >
                <AtSign className="mt-0.5 h-3.5 w-3.5 shrink-0 text-purple-300" />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-1 text-xs">
                    <span className="font-medium text-foreground">@{localPart}</span>
                    <span className="text-muted-foreground">in</span>
                    <span className="font-mono text-[11px] text-muted-foreground">#{n.ticketNumber}</span>
                  </div>
                  <div className="mt-0.5 truncate text-[11px] text-muted-foreground/90">
                    {n.previewText || n.ticketSubject}
                  </div>
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
                className="shrink-0 rounded-md p-1 text-muted-foreground/60 transition-colors hover:bg-white/[0.08] hover:text-foreground opacity-0 group-hover:opacity-100"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </div>
          );
        })}
      </div>
      <div className="flex items-center justify-between border-t border-white/5 px-3 py-2 text-[11px]">
        <button
          type="button"
          onClick={() => ackAll.mutate()}
          disabled={ackAll.isPending || items.length === 0}
          className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-muted-foreground/80 transition-colors hover:bg-white/[0.06] hover:text-foreground disabled:opacity-40"
        >
          <CheckCheck className="h-3.5 w-3.5" /> Ack all
        </button>
        <button
          type="button"
          onClick={() => navigate({ to: "/profile/mentions" })}
          className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-muted-foreground/80 transition-colors hover:bg-white/[0.06] hover:text-foreground"
        >
          All tags <ExternalLink className="h-3 w-3" />
        </button>
      </div>
    </div>
  );
}
