import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Activity, Phone, ChevronRight, Lock } from "lucide-react";
import { activityApi, type ActivityEntry } from "@/lib/api";
import { Skeleton } from "@/components/ui/skeleton";
import { useActivityFeedSignalR } from "@/hooks/useActivityFeedSignalR";

const TILE_LIMIT = 12;

function relativeTime(utc: string): string {
  const diff = Date.now() - new Date(utc).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

function localPartOf(email: string): string {
  const at = email.indexOf("@");
  return at <= 0 ? email : email.substring(0, at);
}

/// One compact row — avatar dot + agent local-part + verb + optional
/// entity link + relative time. Mirrors the look of Zammad's activity
/// feed but keeps to the project's glass-card palette.
function ActivityRow({ entry, onTicketClick }: {
  entry: ActivityEntry;
  onTicketClick: (ticketId: string) => void;
}) {
  const isTelavox = entry.eventType.startsWith("telavox_");
  const isTicket = entry.entityType === "ticket" && entry.entityId !== null;

  return (
    <li className="flex items-start gap-3 border-t border-glass py-2 first:border-t-0">
      <div className="mt-1 flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-primary/20 text-[10px] font-medium uppercase text-primary">
        {localPartOf(entry.agentEmail).substring(0, 2)}
      </div>
      <div className="min-w-0 flex-1 text-sm leading-tight">
        <div className="flex flex-wrap items-baseline gap-x-1.5">
          <span className="font-medium text-foreground">
            {localPartOf(entry.agentEmail)}
          </span>
          <span className="text-muted-foreground">{entry.summary}</span>
          {entry.ticketRestricted ? (
            <span
              className="inline-flex items-center gap-1 italic text-muted-foreground/70"
              title="You don't have access to this ticket's queue"
            >
              <Lock className="h-3 w-3" />
              Restricted ticket
            </span>
          ) : isTicket && entry.ticketNumber !== null ? (
            <button
              type="button"
              onClick={() => onTicketClick(entry.entityId!)}
              className="truncate font-medium text-primary hover:underline"
              title={entry.ticketSubject ?? ""}
            >
              #{entry.ticketNumber}
              {entry.ticketSubject ? ` — ${entry.ticketSubject}` : ""}
            </button>
          ) : null}
          {isTelavox && (
            <span className="inline-flex items-center gap-1 text-emerald-400">
              <Phone className="h-3 w-3" />
              <span className="text-xs text-muted-foreground">phone call</span>
            </span>
          )}
        </div>
        <div className="text-[11px] text-muted-foreground/60">
          {relativeTime(entry.occurredUtc)}
        </div>
      </div>
    </li>
  );
}

export function ActivityFeedTile() {
  const navigate = useNavigate();
  useActivityFeedSignalR(true);

  const q = useQuery({
    queryKey: ["activity", "recent"],
    queryFn: () => activityApi.recent(TILE_LIMIT),
    staleTime: 30_000,
  });

  const items = useMemo(() => (q.data?.items ?? []).slice(0, TILE_LIMIT), [q.data]);

  return (
    <section className="glass-card flex h-full flex-col p-5">
      <header className="mb-3 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Activity className="h-4 w-4 text-primary" />
          Activity feed
        </div>
        <button
          type="button"
          onClick={() => navigate({ to: "/activity" })}
          className="inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs text-muted-foreground hover:bg-glass-hover hover:text-foreground"
        >
          Open page
          <ChevronRight className="h-3 w-3" />
        </button>
      </header>

      {q.isLoading ? (
        <div className="space-y-2">
          {Array.from({ length: 5 }).map((_, i) => (
            <Skeleton key={i} className="h-10 w-full" />
          ))}
        </div>
      ) : items.length === 0 ? (
        <div className="flex flex-1 items-center justify-center text-xs text-muted-foreground">
          No activity yet — events appear here as agents work.
        </div>
      ) : (
        <ul className="min-h-0 flex-1 overflow-y-auto pr-1">
          {items.map((entry) => (
            <ActivityRow
              key={entry.id}
              entry={entry}
              onTicketClick={(ticketId) =>
                navigate({ to: "/tickets/$id" as never, params: { id: ticketId } as never })
              }
            />
          ))}
        </ul>
      )}
    </section>
  );
}
