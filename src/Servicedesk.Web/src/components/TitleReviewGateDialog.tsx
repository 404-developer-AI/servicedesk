import * as React from "react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import type { OpenGateMatch } from "@/lib/ticket-api";

type Props = {
  /// Null hides the dialog. Set to the matched first-open gate to block
  /// the ticket until the agent reviews the title.
  gate: OpenGateMatch | null;
  /// Fires when the agent clicks the approve button. Receives the
  /// (possibly edited) subject. The parent applies it server-side.
  onConfirm: (subject: string) => void;
  /// Disables the approve button while the confirmation request is in
  /// flight so a double-click can't fire two PATCHes.
  submitting?: boolean;
};

/// Blocking first-open title-review dialog. Shown once per ticket the
/// first time an agent opens a ticket matching a gate:first_open trigger.
/// The agent reviews or edits the title and clicks the single approve
/// button — there is no cancel path: the dialog refuses Esc / overlay /
/// close-button dismissal so the title is always vetted before the agent
/// can work the ticket. Controlled by `gate`; the parent swaps it to null
/// once the confirmation succeeds.
export function TitleReviewGateDialog({ gate, onConfirm, submitting }: Props) {
  const [subject, setSubject] = React.useState("");

  // Re-seed the editable field whenever a new gate opens so it starts from
  // the ticket's current subject.
  React.useEffect(() => {
    if (gate) setSubject(gate.currentSubject);
  }, [gate?.triggerId, gate?.currentSubject]);

  if (!gate) return null;

  const trimmed = subject.trim();
  const canConfirm = trimmed.length > 0 && !submitting;

  function handleConfirm() {
    if (!canConfirm) return;
    onConfirm(trimmed);
  }

  return (
    <Dialog open={!!gate}>
      <DialogContent
        className="sm:max-w-lg border border-glass bg-popover/95 backdrop-blur-xl [&>button]:hidden"
        onEscapeKeyDown={(e) => e.preventDefault()}
        onInteractOutside={(e) => e.preventDefault()}
        onPointerDownOutside={(e) => e.preventDefault()}
      >
        <DialogHeader>
          <DialogTitle>{gate.title}</DialogTitle>
          {gate.message ? (
            <DialogDescription className="whitespace-pre-wrap text-sm text-muted-foreground">
              {gate.message}
            </DialogDescription>
          ) : null}
        </DialogHeader>

        <div className="space-y-1.5">
          <label className="text-xs font-medium text-muted-foreground">
            {gate.fieldLabel}
          </label>
          <input
            type="text"
            value={subject}
            autoFocus
            onChange={(e) => setSubject(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                handleConfirm();
              }
            }}
            className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
          />
        </div>

        <DialogFooter className="gap-2 sm:gap-2">
          <Button
            onClick={handleConfirm}
            disabled={!canConfirm}
            className="bg-gradient-to-r from-violet-600 to-indigo-600 hover:from-violet-500 hover:to-indigo-500 text-white shadow-[0_0_20px_rgba(124,58,237,0.3)]"
          >
            {gate.confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
