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
/// primary while following, muted otherwise). While the page
/// is open on a followed ticket it also marks the ticket seen: once on open
/// and again shortly after every TicketUpdated, so changes made while you
/// look at it (your own included) never light up the Following dot.
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
    markSeen();
    const hub = getConnection();
    let timer: ReturnType<typeof setTimeout> | null = null;
    const onUpdated = (id: string) => {
      if (id !== ticketId) return;
      if (timer) clearTimeout(timer);
      timer = setTimeout(markSeen, 1500);
    };
    hub.on("TicketUpdated", onUpdated);
    return () => {
      hub.off("TicketUpdated", onUpdated);
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
