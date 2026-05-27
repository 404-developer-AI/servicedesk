import * as React from "react";
import { Link, useNavigate, useRouterState } from "@tanstack/react-router";
import { useQueryClient } from "@tanstack/react-query";
import { GripVertical, Ticket, X } from "lucide-react";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";
import { getConnection } from "@/hooks/usePresence";
import { ticketApi } from "@/lib/ticket-api";
import { taxonomyApi, type Status } from "@/lib/api";
import { cn } from "@/lib/utils";

export function RecentTickets({ collapsed }: { collapsed: boolean }) {
  const tickets = useRecentTicketsStore((s) => s.recentTickets);
  const removeTicket = useRecentTicketsStore((s) => s.removeTicket);
  const moveTicket = useRecentTicketsStore((s) => s.moveTicket);
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [dragIndex, setDragIndex] = React.useState<number | null>(null);
  const [overIndex, setOverIndex] = React.useState<number | null>(null);

  // Live-refresh the colour bar + tooltip when another agent (or the
  // current user from another window) changes a recent ticket's status.
  // SignalR fires `TicketUpdated(id)` for every field change; we ignore
  // the ones that aren't in our recent list, and for the rest we refresh
  // the snapshot via the (cached) ticket detail + statuses queries.
  React.useEffect(() => {
    const hub = getConnection();
    const onTicketUpdated = async (updatedTicketId: string) => {
      const recent = useRecentTicketsStore.getState().recentTickets;
      const hit = recent.find((t) => t.id === updatedTicketId);
      if (!hit) return;
      try {
        const [detail, statuses] = await Promise.all([
          queryClient.fetchQuery({
            queryKey: ["ticket", updatedTicketId],
            queryFn: () => ticketApi.get(updatedTicketId),
          }),
          queryClient.fetchQuery<Status[]>({
            queryKey: ["statuses"],
            queryFn: taxonomyApi.statuses.list,
            staleTime: 300_000,
          }),
        ]);
        const status = statuses.find((s) => s.id === detail.ticket.statusId);
        useRecentTicketsStore.getState().addTicket({
          id: detail.ticket.id,
          number: detail.ticket.number,
          subject: detail.ticket.subject,
          statusColor: status?.color,
          statusName: status?.name,
          statusStateCategory: status?.stateCategory,
        });
      } catch {
        // Recent-list freshness is best-effort; a failed fetch leaves
        // the stale snapshot in place until the next event or re-view.
      }
    };
    hub.on("TicketUpdated", onTicketUpdated);
    return () => {
      hub.off("TicketUpdated", onTicketUpdated);
    };
  }, [queryClient]);

  if (tickets.length === 0) return null;

  function handleDragStart(e: React.DragEvent, index: number) {
    setDragIndex(index);
    e.dataTransfer.effectAllowed = "move";
    // Make the drag image semi-transparent
    if (e.currentTarget instanceof HTMLElement) {
      e.dataTransfer.setDragImage(e.currentTarget, 0, 0);
    }
  }

  function handleDragOver(e: React.DragEvent, index: number) {
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    setOverIndex(index);
  }

  function handleDrop(e: React.DragEvent, toIndex: number) {
    e.preventDefault();
    if (dragIndex !== null && dragIndex !== toIndex) {
      moveTicket(dragIndex, toIndex);
    }
    setDragIndex(null);
    setOverIndex(null);
  }

  function handleDragEnd() {
    setDragIndex(null);
    setOverIndex(null);
  }

  return (
    <div className="mt-2 flex min-h-0 flex-1 flex-col border-t border-glass pt-2">
      {!collapsed && (
        <div className="px-3 pb-1 text-[10px] font-medium uppercase tracking-widest text-muted-foreground/60">
          Recent
        </div>
      )}
      <div
        className="min-h-0 flex-1 space-y-0.5 overflow-y-auto pr-0.5"
        onDragOver={(e) => e.preventDefault()}
      >
        {tickets.map((t, index) => {
          const href = `/tickets/${t.id}`;
          const active = pathname === href;
          const isDragging = dragIndex === index;
          const isOver = overIndex === index && dragIndex !== index;

          const statusTint = t.statusColor;
          const collapsedTitle = collapsed
            ? t.statusName
              ? `#${t.number} — ${t.subject} (${t.statusName})`
              : `#${t.number} — ${t.subject}`
            : t.statusName ?? undefined;

          return (
            <div
              key={t.id}
              draggable={!collapsed}
              onDragStart={(e) => handleDragStart(e, index)}
              onDragOver={(e) => handleDragOver(e, index)}
              onDrop={(e) => handleDrop(e, index)}
              onDragEnd={handleDragEnd}
              // The inset bar is drawn via boxShadow so it follows the
              // rounded-lg corners and doesn't shift content the way a
              // border-left would. The active-row ring is composed in
              // the same shadow stack to keep both effects visible.
              style={
                statusTint
                  ? {
                      boxShadow: active
                        ? `inset 3px 0 0 0 ${statusTint}, inset 0 0 0 1px hsl(var(--border))`
                        : `inset 3px 0 0 0 ${statusTint}`,
                    }
                  : undefined
              }
              className={cn(
                "group flex items-center rounded-lg transition-colors",
                active
                  ? "bg-glass-strong text-foreground"
                  : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
                // Without an inline boxShadow the active row keeps its
                // original ring class. With one, the shadow already
                // includes the ring, so skip the class to avoid stacking.
                active && !statusTint && "shadow-[inset_0_0_0_1px_hsl(var(--border))]",
                collapsed ? "justify-center px-2 py-2" : "gap-1 px-1 py-1.5",
                isDragging && "opacity-40",
                isOver && "border-t-2 border-primary/50",
              )}
              title={collapsedTitle}
            >
              {!collapsed && (
                <span className="shrink-0 cursor-grab active:cursor-grabbing text-muted-foreground/30 hover:text-muted-foreground/60 px-1">
                  <GripVertical className="h-3 w-3" />
                </span>
              )}
              <Link
                to="/tickets/$ticketId"
                params={{ ticketId: t.id }}
                className={cn(
                  "flex min-w-0 flex-1 items-center gap-2",
                  collapsed && "justify-center",
                )}
                title={collapsed ? collapsedTitle : undefined}
                draggable={false}
              >
                <Ticket
                  className={cn(
                    "h-3.5 w-3.5 shrink-0",
                    !statusTint && active && "text-primary",
                  )}
                  style={statusTint ? { color: statusTint } : undefined}
                />
                {!collapsed && (
                  <>
                    <span className="font-mono text-xs shrink-0">#{t.number}</span>
                    <span className="truncate text-xs">{t.subject}</span>
                  </>
                )}
              </Link>
              {!collapsed && (
                <button
                  type="button"
                  draggable={false}
                  onClick={(e) => {
                    e.stopPropagation();
                    removeTicket(t.id);
                    if (pathname === `/tickets/${t.id}`) {
                      navigate({ to: "/tickets" });
                    }
                  }}
                  className="hidden shrink-0 rounded p-0.5 hover:bg-glass-hover group-hover:inline-flex"
                  aria-label={`Remove #${t.number} from recent`}
                >
                  <X className="h-3 w-3" />
                </button>
              )}
            </div>
          );
        })}
        {/* Drop zone after the last item */}
        {!collapsed && dragIndex !== null && (
          <div
            className={cn(
              "h-6 rounded-lg transition-colors",
              overIndex === tickets.length && "bg-primary/10 border border-dashed border-primary/30",
            )}
            onDragOver={(e) => {
              e.preventDefault();
              e.dataTransfer.dropEffect = "move";
              setOverIndex(tickets.length);
            }}
            onDrop={(e) => {
              e.preventDefault();
              if (dragIndex !== null) {
                const target = dragIndex < tickets.length - 1 ? tickets.length - 1 : dragIndex;
                moveTicket(dragIndex, target);
              }
              setDragIndex(null);
              setOverIndex(null);
            }}
          />
        )}
      </div>
    </div>
  );
}
