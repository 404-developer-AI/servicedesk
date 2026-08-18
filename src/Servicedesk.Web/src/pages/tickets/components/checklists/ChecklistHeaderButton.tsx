import { ListChecks, Plus } from "lucide-react";
import { cn } from "@/lib/utils";
import type { TicketChecklist } from "@/lib/checklist-api";
import { AttachChecklistMenu } from "./AttachChecklistMenu";
import { summarizeChecklists } from "./useTicketChecklists";

/// Header action next to the SLA pill / PDF export. Without checklists it
/// is the "Add checklist" entry point; with checklists it is the at-a-glance
/// progress chip (amber while open, emerald when everything is done) that
/// opens the docked panel — so an unfinished checklist is visible the moment
/// the ticket opens.
export function ChecklistHeaderButton({
  ticketId,
  checklists,
  maxPerTicket,
  onOpen,
  onAttached,
}: {
  ticketId: string;
  checklists: TicketChecklist[];
  maxPerTicket: number;
  onOpen: () => void;
  onAttached: (checklist: TicketChecklist) => void;
}) {
  const s = summarizeChecklists(checklists);
  if (s.count === 0) {
    return (
      <AttachChecklistMenu ticketId={ticketId} attachedCount={0} maxPerTicket={maxPerTicket} onAttached={onAttached}>
        <button
          type="button"
          className="inline-flex shrink-0 items-center gap-1.5 rounded-md border border-glass bg-glass px-2.5 py-1 text-xs text-muted-foreground transition-colors hover:text-foreground hover:bg-glass-hover"
          title="Attach a checklist to this ticket"
        >
          <ListChecks className="h-3.5 w-3.5" />
          <span className="hidden lg:inline">Add checklist</span>
          <Plus className="h-3 w-3 lg:hidden" />
        </button>
      </AttachChecklistMenu>
    );
  }
  const complete = s.allComplete;
  return (
    <button
      type="button"
      onClick={onOpen}
      className={cn(
        "inline-flex shrink-0 items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium tabular-nums transition-colors",
        complete
          ? "border-emerald-400/30 bg-emerald-400/10 text-emerald-200 hover:bg-emerald-400/20"
          : "border-amber-400/40 bg-amber-400/15 text-amber-100 hover:bg-amber-400/25",
      )}
      title={
        complete
          ? `${s.count} checklist${s.count === 1 ? "" : "s"} — all required items done`
          : `${s.requiredTotal - s.requiredDone} required checklist item${s.requiredTotal - s.requiredDone === 1 ? "" : "s"} still open — click to open`
      }
    >
      <ListChecks className="h-3.5 w-3.5" />
      <span>
        {s.requiredDone}/{s.requiredTotal}
      </span>
      {s.count > 1 && <span className="opacity-70">· {s.count} lists</span>}
    </button>
  );
}
