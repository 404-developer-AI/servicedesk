import { PhoneCall } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";

/// v0.1.17 — asked every time an agent opens a ticket that carries the
/// Call-back flag (setting Tickets.CallbackOpenPromptEnabled). "Keep" just
/// closes it for this open; "Turn off" clears the flag through the normal
/// PATCH path, so it lands in the timeline as a TicketFlagChange event.
export function CallbackPromptDialog({
  open,
  color,
  submitting,
  onTurnOff,
  onKeep,
}: {
  open: boolean;
  color: string;
  submitting: boolean;
  onTurnOff: () => void;
  onKeep: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(o) => !o && !submitting && onKeep()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <span
              aria-hidden
              className="inline-flex h-7 w-7 items-center justify-center rounded-full"
              style={{ backgroundColor: `${color}22`, boxShadow: `0 0 12px ${color}55` }}
            >
              <PhoneCall className="h-3.5 w-3.5" style={{ color }} />
            </span>
            Call-back ticket
          </DialogTitle>
          <DialogDescription>
            This ticket is marked for a call-back. Has the customer been called back? Turn the
            call-back flag off if it is no longer needed.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={onKeep} disabled={submitting}>
            Keep call-back
          </Button>
          <Button onClick={onTurnOff} disabled={submitting}>
            {submitting ? "Turning off..." : "Turn off call-back"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
