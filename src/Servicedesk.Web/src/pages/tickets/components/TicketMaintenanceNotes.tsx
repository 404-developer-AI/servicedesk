import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, Wrench } from "lucide-react";
import { MaintenanceNotesPanel } from "@/components/maintenance-notes/MaintenanceNotesPanel";
import { ticketNotesDoor } from "@/lib/maintenance-notes-api";
import { cn } from "@/lib/utils";

/// v0.1.67 — the maintenance notes of the customer this planner ticket was
/// made for, near the top of the ticket so the technician sees them during
/// the intervention. The notes live on the plan member (one live set across
/// every round), never as a copy on this ticket — editing here edits them
/// for the planner and the next round too. Only planner tickets (source
/// System) ask the server, so a normal ticket open costs no extra request.
export function TicketMaintenanceNotes({ ticketId, source }: { ticketId: string; source: string }) {
  const door = React.useMemo(() => ticketNotesDoor(ticketId), [ticketId]);
  const view = useQuery({ queryKey: door.key, queryFn: door.list, enabled: source === "System", staleTime: 30_000 });
  const [open, setOpen] = React.useState(true);
  const subject = view.data?.subject;
  if (!subject) return null;

  const who = [subject.companyName ?? subject.contactName, subject.label].filter(Boolean).join(" — ");
  const count = view.data?.notes.length ?? 0;

  return (
    <div className="sd-maintenance-notes shrink-0 pb-3">
      <div className="glass-panel overflow-hidden">
        <button type="button" onClick={() => setOpen((o) => !o)} aria-expanded={open}
          className="glass-hover flex w-full items-center gap-2 px-3 py-2 text-left focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring">
          <Wrench className="h-4 w-4 shrink-0 text-primary" />
          <span className="shrink-0 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Maintenance notes</span>
          <span className="min-w-0 truncate text-xs text-muted-foreground">{subject.planName}{who ? ` · ${who}` : ""}</span>
          <span className="ml-auto shrink-0 text-xs text-muted-foreground">{count === 0 ? "none yet" : `${count} note${count === 1 ? "" : "s"}`}</span>
          <ChevronDown className={cn("h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform", open && "rotate-180")} />
        </button>
        {open && (
          <div className="border-t border-glass px-3 py-2.5">
            <MaintenanceNotesPanel door={door} currentTicketId={ticketId}
              emptyHint="No notes for this customer yet. What you write here is shown on every next maintenance ticket of this customer." />
          </div>
        )}
      </div>
    </div>
  );
}
