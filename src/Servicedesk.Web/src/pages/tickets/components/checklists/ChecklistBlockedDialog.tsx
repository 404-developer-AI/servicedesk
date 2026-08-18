import { ListChecks, Lock } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";

export type ChecklistBlocker = { checklistId: string; name: string; openRequired: number };

/// Shown when the agent picks a resolving/closing status while a blocking
/// checklist still has required open items. There is no override on
/// purpose (the escape hatch is marking items not applicable, with a
/// reason, on the checklist itself); the primary action jumps to the panel.
export function ChecklistBlockedDialog({
  open,
  blockers,
  targetStatusName,
  triggerName,
  onOpenChecklist,
  onClose,
}: {
  open: boolean;
  blockers: ChecklistBlocker[];
  targetStatusName: string | null;
  /// Set when a trigger (not the agent) tried the status change.
  triggerName?: string | null;
  onOpenChecklist: (checklistId: string) => void;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(v) => !v && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <span className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-amber-400/30 bg-amber-400/10 text-amber-300">
              <Lock className="h-4 w-4" />
            </span>
            Checklist not finished
          </DialogTitle>
          <DialogDescription>
            {triggerName ? (
              <>
                Trigger <span className="text-foreground/80">“{triggerName}”</span> tried to set the ticket to{" "}
                <span className="text-foreground/80">{targetStatusName ?? "a closing status"}</span>, but a checklist blocked it.
                The ticket stays in its current status.
              </>
            ) : targetStatusName ? (
              <>The ticket can't be set to <span className="text-foreground/80">{targetStatusName}</span> yet.</>
            ) : (
              <>The ticket can't be resolved or closed yet.</>
            )}{" "}
            Finish the required items — or mark them as not applicable with a reason — first.
          </DialogDescription>
        </DialogHeader>
        <ul className="space-y-1.5">
          {blockers.map((b) => (
            <li key={b.checklistId}>
              <button
                type="button"
                onClick={() => onOpenChecklist(b.checklistId)}
                className="w-full flex items-center gap-3 rounded-md border border-glass bg-glass px-3 py-2 text-left glass-hover"
              >
                <ListChecks className="h-4 w-4 shrink-0 text-amber-300/90" />
                <span className="min-w-0 flex-1 truncate text-sm">{b.name}</span>
                <span className="shrink-0 rounded-md border border-amber-400/30 bg-amber-400/10 px-2 py-0.5 text-xs font-medium text-amber-200 tabular-nums">
                  {b.openRequired} open
                </span>
              </button>
            </li>
          ))}
        </ul>
        <DialogFooter className="gap-2 sm:gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="button" onClick={() => blockers[0] && onOpenChecklist(blockers[0].checklistId)} disabled={blockers.length === 0}>
            <ListChecks className="h-4 w-4" />
            Open checklist
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
