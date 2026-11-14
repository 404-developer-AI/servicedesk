import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Eye } from "lucide-react";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { errorMessage } from "@/lib/http";
import { getConnection } from "@/hooks/usePresence";
import { useFollowing } from "@/shell/FollowingWidget";
import { FOLLOWING_KEY, followingApi, markedSeen, withoutTicket, type FollowingResponse } from "@/lib/following-api";

/// v0.1.54 — square Follow toggle next to the ticket number (tinted
/// primary while following, muted otherwise). While a followed ticket is
/// open in a visible, focused tab it also marks the ticket seen: once on
/// open and again shortly after every TicketUpdated, so changes made while
/// you look at it (your own included) never light up the Following dot.
/// A background tab or unfocused window never marks anything seen.
export function FollowButton({ ticketId }: { ticketId: string }) {
  const qc = useQueryClient();
  const { data } = useFollowing();
  const following = !!data?.items.some((i) => i.ticketId === ticketId);

  const markSeen = React.useCallback(() => {
    qc.setQueryData<FollowingResponse>(FOLLOWING_KEY, (old) => markedSeen(old, ticketId));
    followingApi.seen(ticketId)
      .then(() => qc.invalidateQueries({ queryKey: FOLLOWING_KEY }))
      .catch(() => {});
  }, [qc, ticketId]);

  React.useEffect(() => {
    if (!following) return;
    // Only a page someone is actually looking at counts as "seen": a
    // background tab or an unfocused window left open on this ticket must
    // not silently clear the marker. A change that arrives while unwatched
    // stays pending and is marked as soon as the user returns to this tab.
    const watched = () => document.visibilityState === "visible" && document.hasFocus();
    let pending = false;
    const markIfWatched = () => {
      if (watched()) {
        pending = false;
        markSeen();
      } else {
        pending = true;
      }
    };
    markIfWatched();

    const hub = getConnection();
    let timer: ReturnType<typeof setTimeout> | null = null;
    const onUpdated = (id: string) => {
      if (id !== ticketId) return;
      if (timer) clearTimeout(timer);
      timer = setTimeout(markIfWatched, 1500);
    };
    const onReturn = () => {
      if (pending && watched()) markIfWatched();
    };
    hub.on("TicketUpdated", onUpdated);
    window.addEventListener("focus", onReturn);
    document.addEventListener("visibilitychange", onReturn);
    return () => {
      hub.off("TicketUpdated", onUpdated);
      window.removeEventListener("focus", onReturn);
      document.removeEventListener("visibilitychange", onReturn);
      if (timer) clearTimeout(timer);
    };
  }, [following, ticketId, markSeen]);

  const toggle = useMutation({
    mutationFn: () => (following ? followingApi.unfollow(ticketId) : followingApi.follow(ticketId)),
    onMutate: () => {
      if (following) qc.setQueryData<FollowingResponse>(FOLLOWING_KEY, (old) => withoutTicket(old, ticketId));
    },
    onSuccess: () => toast.success(following ? "Stopped following this ticket" : "Following this ticket"),
    onError: (e) => toast.error(errorMessage(e, following ? "Could not unfollow the ticket" : "Could not follow the ticket")),
    onSettled: () => void qc.invalidateQueries({ queryKey: FOLLOWING_KEY }),
  });

  return (
    <button
      type="button"
      onClick={() => toggle.mutate()}
      disabled={toggle.isPending || !data}
      aria-pressed={following}
      aria-label={following ? "Unfollow this ticket" : "Follow this ticket"}
      title={following
        ? "Following — click to unfollow"
        : "Follow this ticket: it shows up under Following and gets a red dot when something happens"}
      className={cn(
        "sd-follow-button flex h-6 w-6 shrink-0 items-center justify-center rounded border transition-colors disabled:opacity-60",
        following
          ? "border-primary/30 bg-primary/10 text-primary hover:bg-primary/15"
          : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
      )}
    >
      <Eye className="h-3.5 w-3.5" />
    </button>
  );
}
