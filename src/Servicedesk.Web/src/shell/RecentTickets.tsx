import * as React from "react";
import { Link, useNavigate, useRouterState } from "@tanstack/react-router";
import { GripVertical, Ticket, X } from "lucide-react";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";
import { cn } from "@/lib/utils";

export function RecentTickets({ collapsed }: { collapsed: boolean }) {
  const tickets = useRecentTicketsStore((s) => s.recentTickets);
  const removeTicket = useRecentTicketsStore((s) => s.removeTicket);
  const moveTicket = useRecentTicketsStore((s) => s.moveTicket);
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const navigate = useNavigate();
  const [dragIndex, setDragIndex] = React.useState<number | null>(null);
  const [overIndex, setOverIndex] = React.useState<number | null>(null);

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
    <div className="mt-2 border-t border-white/5 pt-2">
      {!collapsed && (
        <div className="px-3 pb-1 text-[10px] font-medium uppercase tracking-widest text-muted-foreground/60">
          Recent
        </div>
      )}
      <div
        className="max-h-[40vh] space-y-0.5 overflow-y-auto pr-0.5"
        onDragOver={(e) => e.preventDefault()}
      >
        {tickets.map((t, index) => {
          const href = `/tickets/${t.id}`;
          const active = pathname === href;
          const isDragging = dragIndex === index;
          const isOver = overIndex === index && dragIndex !== index;

          return (
            <div
              key={t.id}
              draggable={!collapsed}
              onDragStart={(e) => handleDragStart(e, index)}
              onDragOver={(e) => handleDragOver(e, index)}
              onDrop={(e) => handleDrop(e, index)}
              onDragEnd={handleDragEnd}
              className={cn(
                "group flex items-center rounded-lg transition-colors",
                active
                  ? "bg-white/[0.07] text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
                  : "text-muted-foreground hover:bg-white/[0.04] hover:text-foreground",
                collapsed ? "justify-center px-2 py-2" : "gap-1 px-1 py-1.5",
                isDragging && "opacity-40",
                isOver && "border-t-2 border-primary/50",
              )}
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
                title={collapsed ? `#${t.number} — ${t.subject}` : undefined}
                draggable={false}
              >
                <Ticket className={cn("h-3.5 w-3.5 shrink-0", active && "text-primary")} />
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
                  className="hidden shrink-0 rounded p-0.5 hover:bg-white/10 group-hover:inline-flex"
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
