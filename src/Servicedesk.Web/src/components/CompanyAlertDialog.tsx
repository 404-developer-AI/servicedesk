import { Link } from "@tanstack/react-router";
import { Bell, ExternalLink } from "lucide-react";
import type { CompanyAlert } from "@/lib/ticket-api";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";

type Props = {
  alert: CompanyAlert;
  open: boolean;
  onClose: () => void;
};

/// v0.0.9: modal that pops up when a ticket is created or opened and the
/// requester's company has a non-empty alert with the matching trigger
/// flag. The alert text is rendered as plain text with whitespace
/// preservation — never as HTML — so notes can't inject markup.
export function CompanyAlertDialog({ alert, open, onClose }: Props) {
  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Bell className="h-4 w-4 text-amber-300" />
            {alert.companyName}
          </DialogTitle>
          <DialogDescription className="font-mono text-xs">
            {alert.code}
          </DialogDescription>
        </DialogHeader>

        <div className="rounded-md border border-amber-400/20 bg-amber-400/[0.05] px-4 py-3 text-sm text-amber-100">
          <div className="whitespace-pre-wrap">{alert.alertText}</div>
        </div>

        <DialogFooter className="sm:justify-between">
          <Link
            to="/companies/$companyId"
            params={{ companyId: alert.companyId }}
            className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
            onClick={onClose}
          >
            Open company <ExternalLink className="h-3 w-3" />
          </Link>
          <Button onClick={onClose}>Got it</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

const STORAGE_KEY_PREFIX = "companyAlert:seen:";

export function hasSeenAlertThisSession(ticketId: string): boolean {
  try {
    return sessionStorage.getItem(STORAGE_KEY_PREFIX + ticketId) === "1";
  } catch {
    return false;
  }
}

export function markAlertSeen(ticketId: string): void {
  try {
    sessionStorage.setItem(STORAGE_KEY_PREFIX + ticketId, "1");
  } catch {
    /* sessionStorage disabled — fall through; next mount will re-show. */
  }
}
