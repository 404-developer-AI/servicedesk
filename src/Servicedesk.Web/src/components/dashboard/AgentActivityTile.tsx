import * as React from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { ChevronRight, Phone, UsersRound } from "lucide-react";
import {
  dashboardApi,
  type AgentActivity,
  type AgentActivitySnapshot,
  type AgentActivityTicket,
} from "@/lib/api";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { getConnection } from "@/hooks/usePresence";
import { cn } from "@/lib/utils";

const QUERY_KEY = ["dashboard", "agent-activity"] as const;

/// Agent + Admin dashboard tile. Master-detail layout: agent roster on
/// the left, the selected agent's tickets on the right. Admin-grantable
/// per user via Settings → Users → Features (since v0.0.44; was admin-
/// only before). Live-updated via the AgentActivity event broadcast by
/// TicketPresenceHub to the "agent-activity-admins" group — each event
/// replaces a single agent's entry in the cached snapshot, no full
/// refetch needed.
export function AgentActivityTile() {
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: QUERY_KEY,
    queryFn: () => dashboardApi.agentActivity(),
  });

  // SignalR live updates: the hub broadcasts a per-agent activity record
  // every time that agent's presence changes (connect, disconnect, open
  // ticket, switch ticket, recent-list change). We merge into the
  // existing snapshot so the right column stays in sync without a
  // full refetch.
  React.useEffect(() => {
    const hub = getConnection();
    const handler = (incoming: AgentActivity) => {
      queryClient.setQueryData<AgentActivitySnapshot | undefined>(
        QUERY_KEY,
        (prev) => {
          if (!prev) return prev;
          const next = prev.agents.slice();
          const idx = next.findIndex((a) => a.userId === incoming.userId);
          if (idx >= 0) {
            next[idx] = incoming;
          } else {
            next.push(incoming);
            // Keep the same email-ordering as the snapshot endpoint so a
            // newly-seen agent doesn't jump to the bottom of the list.
            next.sort((a, b) => a.email.localeCompare(b.email));
          }
          return { agents: next };
        },
      );
    };
    hub.on("AgentActivity", handler);
    return () => {
      hub.off("AgentActivity", handler);
    };
  }, [queryClient]);

  // Selection persists across re-renders but follows the data when
  // available — default to the first agent with an active ticket, else
  // the first online agent, else the first row.
  const [selectedUserId, setSelectedUserId] = React.useState<string | null>(null);

  const agents = React.useMemo(() => sortAgents(query.data?.agents ?? []), [query.data]);

  const selected = React.useMemo(() => {
    if (agents.length === 0) return null;
    const explicit = selectedUserId && agents.find((a) => a.userId === selectedUserId);
    if (explicit) return explicit;
    // Auto-select the busiest agent: on a call > viewing > online > first.
    return (
      agents.find((a) => a.online && a.callState === "answered") ??
      agents.find((a) => a.online && a.viewing) ??
      agents.find((a) => a.online) ??
      agents[0]
    );
  }, [agents, selectedUserId]);

  if (query.isLoading) {
    return (
      <section className="glass-card flex h-full flex-col p-5">
        <header className="mb-4 flex items-center gap-2 text-sm font-medium text-foreground">
          <UsersRound className="h-4 w-4 text-primary" />
          Agent activity
        </header>
        <Skeleton className="h-48 w-full" />
      </section>
    );
  }

  if (query.isError) {
    return (
      <section className="glass-card flex h-full flex-col p-5">
        <header className="mb-4 flex items-center gap-2 text-sm font-medium text-foreground">
          <UsersRound className="h-4 w-4 text-primary" />
          Agent activity
        </header>
        <p className="text-xs text-muted-foreground">
          Unable to load agent activity — check your session.
        </p>
      </section>
    );
  }

  const onlineCount = agents.filter((a) => a.online).length;

  return (
    <section className="glass-card flex h-full flex-col p-5">
      <header className="mb-4 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <UsersRound className="h-4 w-4 text-primary" />
          Agent activity
        </div>
        <Badge className="border border-emerald-400/30 bg-emerald-500/10 text-[11px] font-normal text-emerald-300">
          <span className="mr-1 inline-block h-1.5 w-1.5 rounded-full bg-emerald-400" />
          {onlineCount} online
        </Badge>
      </header>

      {agents.length === 0 ? (
        <p className="text-xs text-muted-foreground">No agents found.</p>
      ) : (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-[minmax(0,1fr)_minmax(0,1.6fr)]">
          <AgentList
            agents={agents}
            selectedUserId={selected?.userId ?? null}
            onSelect={setSelectedUserId}
          />
          <AgentTickets agent={selected} />
        </div>
      )}
    </section>
  );
}

function AgentList({
  agents,
  selectedUserId,
  onSelect,
}: {
  agents: AgentActivity[];
  selectedUserId: string | null;
  onSelect: (userId: string) => void;
}) {
  return (
    <ul className="max-h-[26rem] space-y-1 overflow-y-auto pr-1">
      {agents.map((a) => {
        const ticketCount = (a.viewing ? 1 : 0) + a.recent.length;
        const isSelected = a.userId === selectedUserId;
        return (
          <li key={a.userId}>
            <button
              type="button"
              onClick={() => onSelect(a.userId)}
              className={cn(
                "group flex w-full items-center gap-3 rounded-md border px-3 py-2 text-left transition-colors",
                isSelected
                  ? "border-glass-strong bg-glass"
                  : "border-glass bg-glass hover:border-glass-strong hover:bg-glass-hover",
                !a.online && "opacity-70",
              )}
            >
              <AgentPresenceIndicator online={a.online} callState={a.callState} />
              <span className="min-w-0 flex-1 truncate text-sm text-foreground/90">
                {a.email}
              </span>
              {ticketCount > 0 ? (
                <span className="shrink-0 rounded-full bg-glass-strong px-2 py-0.5 text-[11px] font-medium tabular-nums text-foreground/80">
                  {ticketCount}
                </span>
              ) : null}
              <ChevronRight className="h-3.5 w-3.5 shrink-0 text-muted-foreground/40 transition-transform group-hover:translate-x-0.5" />
            </button>
          </li>
        );
      })}
    </ul>
  );
}

/// Compact left-edge marker on each agent row. Colour + glyph encodes
/// presence + Telavox call state:
///   - offline           → small grey dot
///   - online + answered → red filled phone (agent picked up a call)
///   - online + ringing  → amber phone outline (phone is alerting)
///   - online + idle     → green dot
function AgentPresenceIndicator({
  online,
  callState,
}: {
  online: boolean;
  callState: AgentActivity["callState"];
}) {
  if (!online) {
    return (
      <span
        aria-hidden
        className="inline-block h-2 w-2 shrink-0 rounded-full bg-glass-strong"
        title="Offline"
      />
    );
  }
  if (callState === "answered") {
    return (
      <span
        title="On a call"
        className="inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-rose-500/20 text-rose-300"
      >
        <Phone className="h-2.5 w-2.5" fill="currentColor" />
      </span>
    );
  }
  if (callState === "ringing") {
    return (
      <span
        title="Phone ringing"
        className="inline-flex h-4 w-4 shrink-0 animate-pulse items-center justify-center rounded-full bg-amber-500/20 text-amber-300"
      >
        <Phone className="h-2.5 w-2.5" />
      </span>
    );
  }
  return (
    <span
      aria-hidden
      className="inline-block h-2 w-2 shrink-0 rounded-full bg-emerald-400"
      title="Online"
    />
  );
}

function AgentTickets({ agent }: { agent: AgentActivity | null }) {
  if (!agent) return null;

  const hasAny = agent.viewing !== null || agent.recent.length > 0;
  if (!hasAny) {
    return (
      <div className="flex min-h-[10rem] flex-col items-center justify-center gap-1 rounded-lg border border-glass bg-glass px-4 py-6 text-center">
        <p className="text-sm text-foreground/80">{agent.email}</p>
        <p className="text-xs text-muted-foreground">
          {agent.online ? "No active or recent tickets." : "Currently offline."}
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {agent.viewing ? (
        <div>
          <p className="mb-1.5 text-[11px] uppercase tracking-wider text-muted-foreground/70">
            Actively viewing
          </p>
          <TicketRow ticket={agent.viewing} viewing />
        </div>
      ) : null}
      {agent.recent.length > 0 ? (
        <div>
          <p className="mb-1.5 text-[11px] uppercase tracking-wider text-muted-foreground/70">
            Recent
          </p>
          <ul className="space-y-1.5">
            {agent.recent.map((t) => (
              <li key={t.id}>
                <TicketRow ticket={t} viewing={false} />
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  );
}

function TicketRow({
  ticket,
  viewing,
}: {
  ticket: AgentActivityTicket;
  viewing: boolean;
}) {
  const navigate = useNavigate();
  const statusColor = ticket.statusColor || "#6b7280";

  return (
    <button
      type="button"
      onClick={() =>
        void navigate({
          to: "/tickets/$ticketId",
          params: { ticketId: ticket.id },
        })
      }
      className={cn(
        "group relative flex w-full items-center gap-3 overflow-hidden rounded-md border py-2 pl-4 pr-3 text-left transition-colors",
        viewing
          ? "border-glass bg-glass hover:border-glass-strong hover:bg-glass-hover"
          : "border-glass bg-glass opacity-70 hover:border-glass-strong hover:bg-glass-hover hover:opacity-100",
      )}
    >
      <span
        aria-hidden
        className="absolute inset-y-1 left-1 w-1 rounded-full"
        style={{ backgroundColor: viewing ? statusColor : "rgba(255,255,255,0.15)" }}
      />
      <span
        className={cn(
          "w-12 shrink-0 font-mono text-xs",
          viewing ? "text-primary" : "text-muted-foreground",
        )}
      >
        #{ticket.number}
      </span>
      <span
        className="min-w-0 flex-1 truncate text-sm text-foreground/90"
        title={ticket.subject}
      >
        {ticket.subject}
      </span>
      {viewing ? (
        <span
          className="shrink-0 rounded-full border px-2 py-0.5 text-[10px] font-medium uppercase tracking-wide"
          style={{
            color: statusColor,
            borderColor: hexToRgba(statusColor, 0.4),
            backgroundColor: hexToRgba(statusColor, 0.12),
          }}
        >
          {ticket.statusName}
        </span>
      ) : (
        <span className="shrink-0 text-[10px] uppercase tracking-wide text-muted-foreground/60">
          {ticket.statusName}
        </span>
      )}
    </button>
  );
}

/// Sort agents by how "busy" they look right now so the admin's eye
/// lands on the most-active rows first. Buckets, in order:
///   0. online + answered call (agent is on the phone)
///   1. online + ringing call (phone alerting)
///   2. online + actively viewing a ticket
///   3. online + has recent tickets
///   4. online + idle
///   5. offline
/// Alphabetical within each bucket.
function sortAgents(agents: AgentActivity[]): AgentActivity[] {
  return agents.slice().sort((a, b) => {
    const rank = (x: AgentActivity) => {
      if (!x.online) return 5;
      if (x.callState === "answered") return 0;
      if (x.callState === "ringing") return 1;
      if (x.viewing) return 2;
      if (x.recent.length > 0) return 3;
      return 4;
    };
    const ra = rank(a);
    const rb = rank(b);
    if (ra !== rb) return ra - rb;
    return a.email.localeCompare(b.email);
  });
}

/// Inline alpha-blended versions of the hex status color for the pill
/// background + border. Falls back to a neutral grey for malformed hex.
function hexToRgba(hex: string, alpha: number): string {
  const m = /^#?([0-9a-fA-F]{6})$/.exec(hex);
  if (!m) return `rgba(255,255,255,${alpha})`;
  const v = m[1];
  const r = parseInt(v.slice(0, 2), 16);
  const g = parseInt(v.slice(2, 4), 16);
  const b = parseInt(v.slice(4, 6), 16);
  return `rgba(${r},${g},${b},${alpha})`;
}

