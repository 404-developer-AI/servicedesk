import { useNavigate } from "@tanstack/react-router";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AtSign, CheckCheck, Loader2 } from "lucide-react";
import { notificationApi, type NotificationHistoryCursor, type UserNotification } from "@/lib/notification-api";
import { useServerNowMs, useServerTimeZone } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";
import { useMobileHeader } from "../mobileHeader";
import { emailLocalPart, relativeAge } from "../format";

/// `/m/mentions` — where colleagues tagged me (@@). New (unacknowledged)
/// first, then the history. Opening one marks it viewed and jumps to the
/// exact timeline entry, the same as the desktop bell.
export function MobileMentionsPage() {
  useMobileHeader("Mentions");
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const serverNowMs = useServerNowMs();
  const zone = useServerTimeZone();

  const pending = useQuery({
    queryKey: ["notifications", "pending"],
    queryFn: () => notificationApi.listPending(),
    staleTime: 30_000,
  });
  const history = useInfiniteQuery({
    queryKey: ["notifications", "history", "mobile"],
    initialPageParam: null as NotificationHistoryCursor | null,
    queryFn: ({ pageParam }) => notificationApi.listHistory(pageParam ?? undefined, 30),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    staleTime: 30_000,
  });

  const ackAll = useMutation({
    mutationFn: () => notificationApi.ackAll(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["notifications"] });
    },
  });

  const open = async (n: UserNotification) => {
    if (n.viewedUtc === null) {
      await notificationApi.markViewed(n.id).catch(() => {});
      queryClient.invalidateQueries({ queryKey: ["notifications"] });
    }
    void navigate({ to: "/m/tickets/$ticketId", params: { ticketId: n.ticketId }, hash: `event-${n.eventId}` });
  };

  const pendingIds = new Set((pending.data ?? []).map((n) => n.id));
  const older = (history.data?.pages ?? []).flatMap((p) => p.items).filter((n) => !pendingIds.has(n.id));
  const loading = pending.isLoading || history.isLoading;

  return (
    <div className="space-y-5">
      {loading ? (
        <div className="flex justify-center py-10 text-muted-foreground">
          <Loader2 className="h-5 w-5 animate-spin" />
        </div>
      ) : (pending.data?.length ?? 0) === 0 && older.length === 0 ? (
        <div className="glass-card flex flex-col items-center gap-2 px-6 py-10 text-center">
          <AtSign className="h-6 w-6 text-muted-foreground" />
          <p className="text-sm font-medium">No mentions yet</p>
          <p className="text-xs text-muted-foreground">When a colleague tags you with @@ on a ticket, it shows up here.</p>
        </div>
      ) : null}

      {(pending.data?.length ?? 0) > 0 && (
        <section className="space-y-2">
          <div className="flex items-center justify-between px-1">
            <h2 className="text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">New</h2>
            <button
              type="button"
              onClick={() => ackAll.mutate()}
              disabled={ackAll.isPending}
              className="inline-flex h-9 items-center gap-1.5 px-2 text-[13px] font-medium text-primary disabled:opacity-50"
            >
              <CheckCheck className="h-4 w-4" />
              Dismiss all
            </button>
          </div>
          <MentionList items={pending.data ?? []} onOpen={open} serverNowMs={serverNowMs} zone={zone} highlight />
        </section>
      )}

      {older.length > 0 && (
        <section className="space-y-2">
          <h2 className="px-1 text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">Earlier</h2>
          <MentionList items={older} onOpen={open} serverNowMs={serverNowMs} zone={zone} />
          {history.hasNextPage && (
            <button
              type="button"
              onClick={() => void history.fetchNextPage()}
              disabled={history.isFetchingNextPage}
              className="flex h-12 w-full items-center justify-center gap-2 rounded-xl border border-glass bg-glass text-[14px] font-medium"
            >
              {history.isFetchingNextPage && <Loader2 className="h-4 w-4 animate-spin" />}
              Load more
            </button>
          )}
        </section>
      )}
    </div>
  );
}

function MentionList({
  items,
  onOpen,
  serverNowMs,
  zone,
  highlight = false,
}: {
  items: UserNotification[];
  onOpen: (n: UserNotification) => void;
  serverNowMs: number | null;
  zone: string | null;
  highlight?: boolean;
}) {
  return (
    <ul className="glass-card divide-y divide-glass overflow-hidden">
      {items.map((n) => (
        <li key={n.id}>
          <button type="button" onClick={() => onOpen(n)} className="flex w-full gap-3 px-4 py-3 text-left active:bg-glass-hover">
            <span
              className={cn(
                "mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-full",
                highlight ? "bg-primary text-primary-foreground" : "bg-accent text-primary",
              )}
            >
              <AtSign className="h-4 w-4" />
            </span>
            <span className="min-w-0 flex-1">
              <span className="flex items-center gap-2">
                <span className="truncate text-[14px] font-semibold">@{emailLocalPart(n.sourceUserEmail) || "agent"}</span>
                <span className="font-mono text-[12px] text-muted-foreground">#{n.ticketNumber}</span>
                <span className="ml-auto shrink-0 text-[12px] tabular-nums text-muted-foreground">
                  {relativeAge(n.createdUtc, serverNowMs, zone)}
                </span>
              </span>
              <span className="block truncate text-[13px] text-foreground/85">{n.ticketSubject}</span>
              {n.previewText && <span className="mt-0.5 line-clamp-2 block text-[13px] text-muted-foreground">{n.previewText}</span>}
            </span>
          </button>
        </li>
      ))}
    </ul>
  );
}
