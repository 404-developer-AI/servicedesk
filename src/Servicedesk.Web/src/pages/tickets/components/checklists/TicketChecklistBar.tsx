import { CheckCircle2, ChevronRight, ListChecks, Lock } from "lucide-react";
import { cn } from "@/lib/utils";
import { nextOpenItem, type TicketChecklist } from "@/lib/checklist-api";
import { ProgressRing } from "./ProgressRing";

/// Always-visible summary strip above the Activity feed (same slot/style
/// as "Time logged"): one row per attached checklist with progress ring,
/// required-items count, the next open item ("where I left off") and the
/// close-block marker. Clicking a row opens the docked panel on that
/// checklist. Renders nothing when the ticket has no checklists — the
/// header button carries the "Add checklist" affordance in that case.
export function TicketChecklistBar({
  checklists,
  activeChecklistId,
  onOpen,
}: {
  checklists: TicketChecklist[];
  activeChecklistId: string | null;
  onOpen: (checklistId: string) => void;
}) {
  if (checklists.length === 0) return null;
  return (
    <div className="glass-panel overflow-hidden">
      {checklists.map((c, idx) => {
        const complete = c.completedUtc !== null;
        const next = complete ? null : nextOpenItem(c);
        const open = c.requiredTotal - c.requiredDone;
        return (
          <button
            key={c.id}
            type="button"
            onClick={() => onOpen(c.id)}
            className={cn(
              "w-full flex items-center gap-3 px-3 py-2 text-left glass-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring transition-colors",
              idx > 0 && "border-t border-glass",
              activeChecklistId === c.id && "bg-glass",
            )}
            aria-label={`Open checklist ${c.name}`}
          >
            <ProgressRing done={c.requiredDone} total={c.requiredTotal} size={26} stroke={3}>
              {complete ? (
                <CheckCircle2 className="h-3.5 w-3.5 text-emerald-400" />
              ) : (
                <ListChecks className="h-3 w-3 text-amber-300/90" />
              )}
            </ProgressRing>
            <span className="text-xs uppercase tracking-wider text-muted-foreground shrink-0 hidden sm:inline">
              Checklist
            </span>
            <span className="min-w-0 flex-1 flex items-center gap-2">
              <span className="text-sm font-medium text-foreground truncate">{c.name}</span>
              {next && (
                <span className="hidden md:inline min-w-0 truncate text-xs text-muted-foreground/70">
                  <span className="text-muted-foreground/50">next:</span> {next.title}
                </span>
              )}
            </span>
            <span className="flex shrink-0 items-center gap-1.5">
              {c.blockClose && !complete && (
                <span
                  className="inline-flex items-center gap-1 rounded-md border border-amber-400/30 bg-amber-400/10 px-1.5 py-0.5 text-[10px] font-medium text-amber-200"
                  title="This checklist blocks resolving/closing the ticket until every required item is done or not applicable"
                >
                  <Lock className="h-3 w-3" /> blocks close
                </span>
              )}
              <span
                className={cn(
                  "inline-flex items-center rounded-md border px-2 py-0.5 text-xs font-medium tabular-nums",
                  complete
                    ? "border-emerald-400/30 bg-emerald-400/10 text-emerald-200"
                    : "border-amber-400/30 bg-amber-400/10 text-amber-200",
                )}
                title={complete ? "All required items done" : `${open} required item${open === 1 ? "" : "s"} still open`}
              >
                {c.requiredDone}/{c.requiredTotal}
              </span>
              <ChevronRight className="h-4 w-4 text-muted-foreground/50" />
            </span>
          </button>
        );
      })}
    </div>
  );
}
