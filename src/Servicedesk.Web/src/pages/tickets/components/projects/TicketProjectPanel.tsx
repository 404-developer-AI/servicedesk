import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import {
  DndContext,
  PointerSensor,
  KeyboardSensor,
  closestCenter,
  useSensor,
  useSensors,
  type DragEndEvent,
} from "@dnd-kit/core";
import {
  SortableContext,
  arrayMove,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { CheckCircle2, EyeOff, FolderKanban, GripVertical, Timer, X } from "lucide-react";
import { cn } from "@/lib/utils";
import {
  ticketApi,
  type ProjectLinkedTicket,
  type ProjectOverview,
  type ProjectTimeRow,
} from "@/lib/ticket-api";
import { formatDuration } from "@/lib/timesheet-api";

export const projectOverviewKey = (ticketId: string) =>
  ["ticket", ticketId, "project-overview"] as const;

export function useProjectOverview(ticketId: string, enabled: boolean) {
  return useQuery<ProjectOverview>({
    queryKey: projectOverviewKey(ticketId),
    queryFn: () => ticketApi.projectOverview(ticketId),
    enabled,
    staleTime: 15_000,
  });
}

/// v0.0.105 — the project working surface, docked in the ticket's right
/// column (in place of the details side panel, like the checklist panel).
/// Lists every linked ticket with its context: open tickets first in the
/// manual priority order (drag to reorder — top = highest priority),
/// completed ones below, and the after-calculation time rollup at the
/// bottom (project + linked tickets, split per timesheet task).
export function TicketProjectPanel({
  ticketId,
  ticketNumber,
  onClose,
}: {
  ticketId: string;
  ticketNumber: number;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const overviewQ = useProjectOverview(ticketId, true);
  const overview = overviewQ.data ?? null;

  const openTickets = React.useMemo(
    () =>
      (overview?.tickets ?? []).filter(
        (t) => t.statusStateCategory !== "Resolved" && t.statusStateCategory !== "Closed",
      ),
    [overview],
  );
  const closedTickets = React.useMemo(
    () =>
      (overview?.tickets ?? []).filter(
        (t) => t.statusStateCategory === "Resolved" || t.statusStateCategory === "Closed",
      ),
    [overview],
  );

  // Local order for a snappy drag; re-seeded from the server list on every
  // refetch (SignalR invalidates the ["ticket", id] prefix on mutations).
  const [order, setOrder] = React.useState<string[]>([]);
  React.useEffect(() => {
    setOrder(openTickets.map((t) => t.id));
  }, [openTickets]);
  const orderedOpen = React.useMemo(() => {
    const byId = new Map(openTickets.map((t) => [t.id, t]));
    const ordered = order.map((id) => byId.get(id)).filter(Boolean) as ProjectLinkedTicket[];
    // New rows that arrived after the last drag append at the end.
    for (const t of openTickets) if (!order.includes(t.id)) ordered.push(t);
    return ordered;
  }, [openTickets, order]);

  const reorder = useMutation({
    mutationFn: (orderedIds: string[]) => ticketApi.reorderProject(ticketId, orderedIds),
    onError: () => {
      toast.error("Could not save the new order");
      queryClient.invalidateQueries({ queryKey: projectOverviewKey(ticketId) });
    },
  });

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const onDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const oldIndex = orderedOpen.findIndex((t) => t.id === active.id);
    const newIndex = orderedOpen.findIndex((t) => t.id === over.id);
    if (oldIndex < 0 || newIndex < 0) return;
    const next = arrayMove(orderedOpen, oldIndex, newIndex).map((t) => t.id);
    setOrder(next);
    // Persist the full visible order; completed tickets keep their stored
    // position (they sort below open ones regardless).
    reorder.mutate(next);
  };

  return (
    <div className="glass-panel flex h-full min-h-0 flex-col overflow-hidden">
      {/* Panel header */}
      <div className="flex items-center gap-2 border-b border-glass px-3 py-2">
        <FolderKanban className="h-4 w-4 text-sky-300/90" />
        <span className="text-xs uppercase tracking-wider text-muted-foreground">Project</span>
        <span className="text-[10px] text-muted-foreground/60">
          Internal — not visible to customers
        </span>
        <span className="ml-auto flex items-center gap-0.5">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1.5 text-muted-foreground/70 hover:text-foreground hover:bg-glass-hover"
            title="Back to ticket details"
            aria-label="Back to ticket details"
          >
            <X className="h-4 w-4" />
          </button>
        </span>
      </div>

      <div className="min-h-0 flex-1 space-y-4 overflow-y-auto px-3 py-3">
        {overviewQ.isLoading && (
          <div className="py-6 text-center text-xs text-muted-foreground/70">
            Loading project overview...
          </div>
        )}
        {overviewQ.isError && (
          <div className="py-6 text-center text-xs text-rose-300/90">
            Could not load the project overview.
          </div>
        )}

        {overview && (
          <>
            {/* Open linked tickets — draggable priority order */}
            <section>
              <div className="mb-1.5 flex items-baseline gap-2">
                <span className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
                  To follow up
                </span>
                <span className="text-[10px] text-muted-foreground/50">
                  {orderedOpen.length === 0
                    ? "none"
                    : "drag to prioritize — top first"}
                </span>
              </div>
              {orderedOpen.length === 0 ? (
                <div className="rounded-md border border-glass bg-glass px-3 py-2.5 text-[11px] italic text-muted-foreground/60">
                  No open tickets linked to this project yet. Use “Link to
                  project” on a ticket to add it here.
                </div>
              ) : (
                <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
                  <SortableContext items={orderedOpen.map((t) => t.id)} strategy={verticalListSortingStrategy}>
                    <ul className="space-y-1.5">
                      {orderedOpen.map((t, i) => (
                        <SortableTicketRow key={t.id} ticket={t} index={i} />
                      ))}
                    </ul>
                  </SortableContext>
                </DndContext>
              )}
            </section>

            {/* Completed linked tickets */}
            {closedTickets.length > 0 && (
              <section>
                <div className="mb-1.5 flex items-baseline gap-2">
                  <span className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
                    Completed
                  </span>
                  <span className="text-[10px] text-muted-foreground/50">
                    kept for after-calculation
                  </span>
                </div>
                <ul className="space-y-1.5">
                  {closedTickets.map((t) => (
                    <li key={t.id}>
                      <TicketRowCard ticket={t} completed />
                    </li>
                  ))}
                </ul>
              </section>
            )}

            {overview.hiddenTicketCount > 0 && (
              <div className="flex items-center gap-1.5 text-[11px] text-muted-foreground/60">
                <EyeOff className="h-3 w-3" aria-hidden />
                {overview.hiddenTicketCount} linked ticket
                {overview.hiddenTicketCount === 1 ? "" : "s"} in queues you cannot
                access {overview.hiddenTicketCount === 1 ? "is" : "are"} not shown.
              </div>
            )}

            <ProjectTimeSummary
              projectTicketId={ticketId}
              projectTicketNumber={ticketNumber}
              tickets={overview.tickets}
              timeRows={overview.timeRows}
            />
          </>
        )}
      </div>
    </div>
  );
}

function SortableTicketRow({ ticket, index }: { ticket: ProjectLinkedTicket; index: number }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } =
    useSortable({ id: ticket.id });
  const style = { transform: CSS.Transform.toString(transform), transition };
  return (
    <li ref={setNodeRef} style={style} className={cn(isDragging && "z-10 opacity-80")}>
      <TicketRowCard
        ticket={ticket}
        priorityPosition={index + 1}
        dragHandle={
          <button
            type="button"
            className="cursor-grab touch-none rounded p-1 text-muted-foreground/40 hover:text-foreground active:cursor-grabbing"
            title="Drag to change priority"
            aria-label="Drag to change priority"
            {...attributes}
            {...listeners}
          >
            <GripVertical className="h-3.5 w-3.5" />
          </button>
        }
      />
    </li>
  );
}

function TicketRowCard({
  ticket,
  completed = false,
  priorityPosition,
  dragHandle,
}: {
  ticket: ProjectLinkedTicket;
  completed?: boolean;
  priorityPosition?: number;
  dragHandle?: React.ReactNode;
}) {
  const requester = ticket.requesterName ?? ticket.requesterEmail ?? "—";
  return (
    <div
      className={cn(
        "flex items-start gap-1.5 rounded-md border border-glass bg-glass px-2 py-2 transition-colors hover:bg-glass-hover",
        completed && "opacity-70",
      )}
    >
      {dragHandle}
      {priorityPosition !== undefined && (
        <span
          className="mt-0.5 inline-flex h-4 w-4 shrink-0 items-center justify-center rounded bg-glass-strong text-[9px] font-semibold text-muted-foreground/80"
          title={`Priority ${priorityPosition}`}
        >
          {priorityPosition}
        </span>
      )}
      {completed && (
        <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-emerald-400/80" aria-hidden />
      )}
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-1.5 text-xs">
          <span
            className="inline-flex h-1.5 w-1.5 shrink-0 rounded-full"
            style={{ backgroundColor: ticket.statusColor }}
            aria-hidden
          />
          <Link
            to="/tickets/$ticketId"
            params={{ ticketId: ticket.id }}
            className="font-medium text-primary hover:underline"
          >
            #{ticket.number}
          </Link>
          <span className="truncate text-foreground/85" title={ticket.subject}>
            {ticket.subject}
          </span>
        </div>
        <div className="mt-0.5 truncate text-[11px] text-muted-foreground/70">
          {ticket.statusName} · {requester}
          {ticket.assigneeName ? ` · ${ticket.assigneeName}` : " · unassigned"}
          {` · ${ticket.queueName}`}
        </div>
      </div>
    </div>
  );
}

/// After-calculation block: total logged time over the project ticket and
/// every linked ticket, split per ticket and per timesheet task — the same
/// numbers the per-ticket Time-logged panel shows, aggregated project-wide.
function ProjectTimeSummary({
  projectTicketId,
  projectTicketNumber,
  tickets,
  timeRows,
}: {
  projectTicketId: string;
  projectTicketNumber: number;
  tickets: ProjectLinkedTicket[];
  timeRows: ProjectTimeRow[];
}) {
  const { perTicket, perTask, total } = React.useMemo(() => {
    const perTicket = new Map<string, number>();
    const perTask = new Map<string, number>();
    let total = 0;
    for (const r of timeRows) {
      perTicket.set(r.ticketId, (perTicket.get(r.ticketId) ?? 0) + r.minutes);
      perTask.set(r.taskName, (perTask.get(r.taskName) ?? 0) + r.minutes);
      total += r.minutes;
    }
    return { perTicket, perTask, total };
  }, [timeRows]);

  const ticketLabel = (id: string): string => {
    if (id === projectTicketId) return `#${projectTicketNumber} (this project)`;
    const t = tickets.find((x) => x.id === id);
    return t ? `#${t.number}` : "hidden ticket";
  };

  const ticketRows = [...perTicket.entries()].sort((a, b) => b[1] - a[1]);
  const taskRows = [...perTask.entries()].sort((a, b) => b[1] - a[1]);

  return (
    <section className="rounded-md border border-glass-strong bg-glass px-2.5 py-2">
      <div className="flex items-center gap-1.5">
        <Timer className="h-3.5 w-3.5 text-muted-foreground/70" aria-hidden />
        <span className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
          Time logged
        </span>
        <span className="ml-auto text-xs font-semibold text-foreground/90">
          {formatDuration(total)}
        </span>
      </div>
      {total === 0 ? (
        <div className="pt-1.5 text-[11px] italic text-muted-foreground/50">
          No time logged on this project or its linked tickets yet.
        </div>
      ) : (
        <div className="grid grid-cols-2 gap-3 pt-2">
          <div>
            <div className="mb-1 text-[9px] uppercase tracking-wider text-muted-foreground/50">
              Per ticket
            </div>
            <ul className="space-y-0.5">
              {ticketRows.map(([id, minutes]) => (
                <li key={id} className="flex items-baseline justify-between gap-2 text-[11px]">
                  <span className="truncate text-muted-foreground/80">{ticketLabel(id)}</span>
                  <span className="shrink-0 tabular-nums text-foreground/85">
                    {formatDuration(minutes)}
                  </span>
                </li>
              ))}
            </ul>
          </div>
          <div>
            <div className="mb-1 text-[9px] uppercase tracking-wider text-muted-foreground/50">
              Per registration type
            </div>
            <ul className="space-y-0.5">
              {taskRows.map(([task, minutes]) => (
                <li key={task} className="flex items-baseline justify-between gap-2 text-[11px]">
                  <span className="truncate text-muted-foreground/80">{task}</span>
                  <span className="shrink-0 tabular-nums text-foreground/85">
                    {formatDuration(minutes)}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        </div>
      )}
    </section>
  );
}
