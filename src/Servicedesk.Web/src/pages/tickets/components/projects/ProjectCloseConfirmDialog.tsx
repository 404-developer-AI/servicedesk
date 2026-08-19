import { AlertTriangle } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";

/// v0.0.105 — soft confirmation (no hard block) when a project ticket is
/// moved into Resolved/Closed while linked tickets are still open. The
/// linked tickets themselves are untouched either way.
export function ProjectCloseConfirmDialog({
  open,
  openTicketCount,
  targetStatusName,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  openTicketCount: number;
  targetStatusName: string | null;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(o) => !o && onCancel()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <AlertTriangle className="h-4 w-4 text-amber-400" />
            Close this project?
          </DialogTitle>
          <DialogDescription>
            {openTicketCount} linked ticket{openTicketCount === 1 ? " is" : "s are"} still
            open on this project. They stay open and keep their project link —
            only this project ticket moves to{" "}
            {targetStatusName ? `“${targetStatusName}”` : "the new status"}.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={onCancel}>
            Keep project open
          </Button>
          <Button onClick={onConfirm}>Change status anyway</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
