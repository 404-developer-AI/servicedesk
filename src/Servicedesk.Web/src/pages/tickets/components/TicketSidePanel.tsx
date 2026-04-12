import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { taxonomyApi } from "@/lib/api";
import { AgentPicker } from "@/components/AgentPicker";
import { cn } from "@/lib/utils";
import type { Ticket, TicketFieldUpdate } from "@/lib/ticket-api";

type TicketSidePanelProps = {
  ticket: Ticket;
  onUpdate: (fields: TicketFieldUpdate) => Promise<void>;
};

const SELECT_CLASS =
  "w-full h-9 px-2 text-sm rounded-md border border-white/10 bg-white/[0.04] text-foreground outline-none focus:border-primary/60 cursor-pointer";

function FieldLabel({ children }: { children: React.ReactNode }) {
  return (
    <div className="text-xs uppercase tracking-wider text-muted-foreground mb-1">
      {children}
    </div>
  );
}

function ColorDot({ color }: { color: string }) {
  return (
    <span
      className="inline-block w-2 h-2 rounded-full shrink-0"
      style={{ backgroundColor: color || "#888" }}
    />
  );
}

function formatDate(iso: string | null | undefined): string {
  if (!iso) return "Not set";
  return new Date(iso).toLocaleString("en-GB", {
    day: "2-digit",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  });
}

function SourceBadge({ source }: { source: string }) {
  return (
    <span className="inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium border border-white/10 bg-white/[0.05] text-muted-foreground">
      {source}
    </span>
  );
}

export function TicketSidePanel({ ticket, onUpdate }: TicketSidePanelProps) {
  const { data: queues } = useQuery({
    queryKey: ["queues"],
    queryFn: taxonomyApi.queues.list,
    staleTime: 60_000,
  });

  const { data: priorities } = useQuery({
    queryKey: ["priorities"],
    queryFn: taxonomyApi.priorities.list,
    staleTime: 60_000,
  });

  const { data: statuses } = useQuery({
    queryKey: ["statuses"],
    queryFn: taxonomyApi.statuses.list,
    staleTime: 60_000,
  });

  const { data: categories } = useQuery({
    queryKey: ["categories"],
    queryFn: taxonomyApi.categories.list,
    staleTime: 60_000,
  });

  const currentStatus = statuses?.find((s) => s.id === ticket.statusId);
  const currentPriority = priorities?.find((p) => p.id === ticket.priorityId);
  const currentQueue = queues?.find((q) => q.id === ticket.queueId);

  return (
    <div className="glass-card p-4 space-y-4 w-[320px] shrink-0">
      <div>
        <FieldLabel>Status</FieldLabel>
        <div className="relative">
          {currentStatus && (
            <span className="absolute left-2 top-1/2 -translate-y-1/2 pointer-events-none z-10">
              <ColorDot color={currentStatus.color} />
            </span>
          )}
          <select
            value={ticket.statusId}
            onChange={(e) => onUpdate({ statusId: e.target.value })}
            className={cn(SELECT_CLASS, currentStatus && "pl-6")}
          >
            {statuses?.map((s) => (
              <option key={s.id} value={s.id} className="bg-background">
                {s.name} ({s.stateCategory})
              </option>
            ))}
          </select>
        </div>
      </div>

      <div>
        <FieldLabel>Priority</FieldLabel>
        <div className="relative">
          {currentPriority && (
            <span className="absolute left-2 top-1/2 -translate-y-1/2 pointer-events-none z-10">
              <ColorDot color={currentPriority.color} />
            </span>
          )}
          <select
            value={ticket.priorityId}
            onChange={(e) => onUpdate({ priorityId: e.target.value })}
            className={cn(SELECT_CLASS, currentPriority && "pl-6")}
          >
            {priorities?.map((p) => (
              <option key={p.id} value={p.id} className="bg-background">
                {p.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div>
        <FieldLabel>Queue</FieldLabel>
        <div className="relative">
          {currentQueue && (
            <span className="absolute left-2 top-1/2 -translate-y-1/2 pointer-events-none z-10">
              <ColorDot color={currentQueue.color} />
            </span>
          )}
          <select
            value={ticket.queueId}
            onChange={(e) => onUpdate({ queueId: e.target.value })}
            className={cn(SELECT_CLASS, currentQueue && "pl-6")}
          >
            {queues?.map((q) => (
              <option key={q.id} value={q.id} className="bg-background">
                {q.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div>
        <FieldLabel>Category</FieldLabel>
        <select
          value={ticket.categoryId ?? ""}
          onChange={(e) =>
            onUpdate({ categoryId: e.target.value || undefined })
          }
          className={SELECT_CLASS}
        >
          <option value="" className="bg-background">
            None
          </option>
          {categories?.map((c) => (
            <option key={c.id} value={c.id} className="bg-background">
              {c.name}
            </option>
          ))}
        </select>
      </div>

      <div>
        <FieldLabel>Assignee</FieldLabel>
        <AgentPicker
          value={ticket.assigneeUserId}
          onChange={(userId) => onUpdate({ assigneeUserId: userId ?? undefined })}
        />
      </div>

      <div>
        <FieldLabel>Requester</FieldLabel>
        <div className="text-sm text-muted-foreground truncate">
          {ticket.requesterContactId}
        </div>
      </div>

      <div className="border-t border-white/10" />

      <div>
        <FieldLabel>Created</FieldLabel>
        <div className="text-sm text-foreground/80">{formatDate(ticket.createdUtc)}</div>
      </div>

      <div>
        <FieldLabel>Updated</FieldLabel>
        <div className="text-sm text-foreground/80">{formatDate(ticket.updatedUtc)}</div>
      </div>

      <div>
        <FieldLabel>Due</FieldLabel>
        <div className="text-sm text-foreground/80">{formatDate(ticket.dueUtc)}</div>
      </div>

      <div>
        <FieldLabel>Source</FieldLabel>
        <SourceBadge source={ticket.source} />
      </div>
    </div>
  );
}
