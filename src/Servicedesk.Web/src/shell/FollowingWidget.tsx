import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Eye, X } from "lucide-react";
import { toast } from "sonner";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import { errorMessage } from "@/lib/http";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDayMonthTime } from "@/lib/dateFormat";
import { colorPillStyle } from "@/lib/colorPill";
import { useTheme } from "@/app/ThemeProvider";
import { getConnection } from "@/hooks/usePresence";
import { ensureNotificationConnection } from "@/hooks/useNotificationSignalR";
import {
  FOLLOWING_KEY, followingApi, markedSeen, withoutTicket,
  type FollowedTicket, type FollowingResponse,
} from "@/lib/following-api";
import { PulseDot } from "@/shell/PulseDot";

/// The shared Following query — the sidebar block and the Follow button in
/// the ticket header read the same cache entry.
export function useFollowing() {
  return useQuery({
    queryKey: FOLLOWING_KEY,
    queryFn: () => followingApi.list(),
    refetchOnWindowFocus: true,
    staleTime: 30_000,
  });
}

/// Live refresh: every write path on the server already pings
/// TicketListUpdated(ticketId) to all staff connections; we refetch (debounced)
/// only when the ticket is one we follow. FollowingChanged comes from our own
/// other tabs following / unfollowing.
function useFollowingRealtime(followedIds: Set<string>) {
  const qc = useQueryClient();
  const idsRef = React.useRef(followedIds);
  idsRef.current = followedIds;

  React.useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | null = null;
    const refresh = () => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        timer = null;
        void qc.invalidateQueries({ queryKey: FOLLOWING_KEY });
      }, 1000);
    };
    const onListUpdated = (ticketId: string) => {
      if (idsRef.current.has(ticketId)) refresh();
    };
    const presence = getConnection();
    const notifications = ensureNotificationConnection();
    presence.on("TicketListUpdated", onListUpdated);
    notifications.on("FollowingChanged", refresh);
    return () => {
      presence.off("TicketListUpdated", onListUpdated);
      notifications.off("FollowingChanged", refresh);
      if (timer) clearTimeout(timer);
    };
  }, [qc]);
}

/// v0.1.54 — the Following block under Group tags. The number is how many
/// tickets you follow; the red dot means something happened on at least one
/// of them since you last opened it.
export function FollowingWidget({ collapsed }: { collapsed: boolean }) {
  const { data, isLoading } = useFollowing();
  const followedIds = React.useMemo(() => new Set((data?.items ?? []).map((i) => i.ticketId)), [data]);
  useFollowingRealtime(followedIds);

  const count = data?.count ?? 0;
  const active = data?.activeCount ?? 0;
  const title = active > 0
    ? `${active} followed ticket${active === 1 ? " has" : "s have"} news`
    : `Following ${count} ticket${count === 1 ? "" : "s"}`;

  return (
    <div className={cn("sd-following mx-3 mb-2", collapsed && "flex justify-center")}>
      <Popover>
        <PopoverTrigger asChild>
          {collapsed ? (
            <button type="button" title={title}
              className={cn(
                "relative flex h-9 w-9 items-center justify-center rounded-lg border border-glass bg-glass text-muted-foreground transition-colors",
                "hover:bg-glass-hover hover:text-foreground",
                active > 0 && "text-primary",
              )}>
              <Eye className="h-4 w-4" />
              {active > 0 ? <PulseDot count={active} /> : null}
            </button>
          ) : (
            <button type="button" title={title}
              className="flex h-8 w-full items-center gap-1 rounded-lg border border-glass bg-glass px-2 text-left transition-colors hover:bg-glass-hover">
              <div className="relative">
                <Eye className={cn("h-3.5 w-3.5", active > 0 ? "text-primary" : "text-muted-foreground")} />
                {active > 0 ? <PulseDot count={null} /> : null}
              </div>
              <span className="text-[10px] font-medium uppercase tracking-[0.06em] text-muted-foreground">Following</span>
              <span className={cn(
                "ml-auto rounded-full border px-1.5 py-px text-[10px] font-medium",
                active > 0
                  ? "border-primary/30 bg-primary/12 text-primary"
                  : "border-glass bg-glass text-muted-foreground",
              )}>
                {count}
              </span>
            </button>
          )}
        </PopoverTrigger>
        <PopoverContent side="right" align="end" className="w-88 p-0">
          <FollowingPanel items={data?.items ?? []} loading={isLoading} />
        </PopoverContent>
      </Popover>
    </div>
  );
}

function FollowingPanel({ items, loading }: { items: FollowedTicket[]; loading: boolean }) {
  const qc = useQueryClient();
  const navigate = useNavigate();
  const zone = useServerTimeZone();
  const theme = useTheme();

  const unfollow = useMutation({
    mutationFn: (ticketId: string) => followingApi.unfollow(ticketId),
    onMutate: (ticketId) => qc.setQueryData<FollowingResponse>(FOLLOWING_KEY, (old) => withoutTicket(old, ticketId)),
    onError: (e) => toast.error(errorMessage(e, "Could not unfollow the ticket")),
    onSettled: () => void qc.invalidateQueries({ queryKey: FOLLOWING_KEY }),
  });

  // The ticket page marks it seen on the server; clear the marker here at
  // once so the dot does not linger while the page loads.
  const open = (t: FollowedTicket) => {
    qc.setQueryData<FollowingResponse>(FOLLOWING_KEY, (old) => markedSeen(old, t.ticketId));
    void navigate({ to: "/tickets/$ticketId", params: { ticketId: t.ticketId } });
  };

  return (
    <div className="flex flex-col">
      {items.length === 0 ? (
        <div className="px-3 py-6 text-center text-xs text-muted-foreground/70">
          {loading ? "Loading…" : "You are not following any tickets. Use Follow in a ticket's header."}
        </div>
      ) : (
        <div className="max-h-[360px] divide-y divide-glass overflow-y-auto">
          {items.map((t) => (
            <div key={t.ticketId}
              className={cn("flex items-start gap-2 px-3 py-2 transition-colors hover:bg-glass-hover", t.hasActivity && "bg-primary/5")}>
              <button type="button" onClick={() => open(t)} className="flex min-w-0 flex-1 items-start gap-2 text-left"
                title={t.hasActivity ? "Something happened since you last opened this ticket" : "No news since you last opened this ticket"}>
                <span className="mt-1.5 flex h-2 w-2 shrink-0 items-center justify-center" aria-hidden>
                  {t.hasActivity ? <span className="h-2 w-2 rounded-full bg-red-500" /> : null}
                </span>
                <div className="min-w-0 flex-auto">
                  <div className="flex items-center gap-1.5 text-xs">
                    <span className={cn("font-mono text-[11px]", t.hasActivity ? "text-foreground" : "text-muted-foreground")}>
                      #{t.ticketNumber}
                    </span>
                    <span className="shrink-0 rounded-md border px-1.5 py-px text-[10px] font-medium"
                      style={colorPillStyle(t.statusColor || "#6b7280", theme)}>
                      {t.statusName}
                    </span>
                  </div>
                  <div className={cn("mt-0.5 truncate text-xs",
                    t.hasActivity ? "font-semibold text-foreground" : "text-muted-foreground")}>
                    {t.subject}
                  </div>
                  <div className="mt-0.5 text-[10px] text-muted-foreground/60">
                    {t.hasActivity ? "Updated " : "Last activity "}{formatDayMonthTime(t.lastActivityUtc, zone)}
                  </div>
                </div>
              </button>
              <button type="button" onClick={() => unfollow.mutate(t.ticketId)} disabled={unfollow.isPending}
                title="Unfollow"
                aria-label={`Unfollow #${t.ticketNumber}`}
                className="shrink-0 rounded-md border border-glass p-1 text-muted-foreground transition-colors hover:border-destructive/40 hover:bg-destructive/10 hover:text-destructive">
                <X className="h-3.5 w-3.5" />
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
