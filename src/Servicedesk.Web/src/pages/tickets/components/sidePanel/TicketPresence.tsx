import { cn } from "@/lib/utils";
import { usePresenceStore, type PresenceUser } from "@/stores/usePresenceStore";
import { useAuth } from "@/auth/authStore";
import { Tooltip, TooltipContent, TooltipTrigger, TooltipProvider } from "@/components/ui/tooltip";

/* ─── Presence ─── */

const EMPTY_PRESENCE: PresenceUser[] = [];

// Live "also viewing" strip. Rendered under the compose form in the
// activity column (not the side panel) so an agent sees who else is in
// the ticket right where they're about to type. Self-hides whenever the
// current user is the only viewer.
export function TicketPresence({ ticketId }: { ticketId: string }) {
  const presence = usePresenceStore((s) => s.byTicket[ticketId] ?? EMPTY_PRESENCE);
  const { user: currentUser } = useAuth();
  const others = presence.filter((u) => u.userId !== currentUser?.id);

  if (others.length === 0) return null;

  return (
    <TooltipProvider delayDuration={200}>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <span className="text-xs uppercase tracking-wider text-muted-foreground">
          Also viewing
        </span>
        {others.map((u) => (
          <PresenceChip key={u.userId} user={u} />
        ))}
      </div>
    </TooltipProvider>
  );
}

function PresenceChip({ user }: { user: PresenceUser }) {
  const initial = user.email.slice(0, 1).toUpperCase();
  const isViewing = user.status === "viewing";

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <div
          className={cn(
            "flex items-center gap-1.5 rounded-full px-2 py-1 text-xs border transition-colors",
            isViewing
              ? "bg-primary/20 border-primary/40 text-foreground"
              : "bg-glass border-glass text-muted-foreground/60",
          )}
        >
          <span
            className={cn(
              "inline-flex h-5 w-5 items-center justify-center rounded-full text-[10px] font-medium",
              isViewing
                ? "bg-primary/40 text-white"
                : "bg-glass-strong text-muted-foreground/50",
            )}
          >
            {initial}
          </span>
          <span className="truncate max-w-[120px]">
            {user.email.split("@")[0]}
          </span>
          <span
            className={cn(
              "h-1.5 w-1.5 rounded-full shrink-0",
              isViewing ? "bg-green-400" : "bg-glass-strong",
            )}
          />
        </div>
      </TooltipTrigger>
      <TooltipContent side="bottom" className="text-xs">
        {user.email} — {isViewing ? "viewing now" : "opened recently"}
      </TooltipContent>
    </Tooltip>
  );
}
