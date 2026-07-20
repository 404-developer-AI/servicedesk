import * as React from "react";
import DOMPurify from "dompurify";
import { Download } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { OpenGateMatch } from "@/lib/ticket-api";
import { useCurrentRole } from "@/hooks/useCurrentRole";

type Props = {
  /// Null hides the dialog. Set to the matched first-open gate to block
  /// the ticket until the agent reviews the title.
  gate: OpenGateMatch | null;
  /// Fires when the agent clicks the approve button. Receives the
  /// (possibly edited) subject. The parent applies it server-side.
  onConfirm: (subject: string) => void;
  /// v0.0.89 — admin-only silent escape hatch. When provided, admins can
  /// close the gate (Shift-gated) without running the confirm actions or
  /// recording anything; the parent just hides it for the session, so the
  /// gate recurs on the next open. Omitted for non-admins → no dismiss path.
  onDismiss?: () => void;
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
export function TitleReviewGateDialog({ gate, onConfirm, onDismiss, submitting }: Props) {
  const [subject, setSubject] = React.useState("");
  // v0.0.89 — the silent-dismiss affordance is admin-only and only exists when
  // the parent wires up onDismiss. The server is never told (the gate is a
  // workflow nudge, not a security control), so client-side gating is enough.
  const isAdmin = useCurrentRole() === "Admin";
  const adminCanDismiss = isAdmin && !!onDismiss;

  // Re-seed the editable field whenever a new gate opens so it starts from
  // the ticket's current subject.
  React.useEffect(() => {
    if (gate) setSubject(gate.currentSubject);
  }, [gate?.triggerId, gate?.currentSubject]);

  // Live "is Shift down" flag so the dismiss button can hint while held — and
  // stay disabled otherwise, keeping the bypass of this deliberately-blocking
  // gate a conscious gesture. Only wired up for admins, only while open.
  const [shiftHeld, setShiftHeld] = React.useState(false);
  React.useEffect(() => {
    if (!gate || !adminCanDismiss) {
      setShiftHeld(false);
      return;
    }
    const onDown = (e: KeyboardEvent) => {
      if (e.key === "Shift") setShiftHeld(true);
    };
    const onUp = (e: KeyboardEvent) => {
      if (e.key === "Shift") setShiftHeld(false);
    };
    window.addEventListener("keydown", onDown);
    window.addEventListener("keyup", onUp);
    return () => {
      window.removeEventListener("keydown", onDown);
      window.removeEventListener("keyup", onUp);
    };
  }, [gate, adminCanDismiss]);

  // Sanitised original-request preview. Memoised on the raw HTML so
  // DOMPurify doesn't re-run (and React doesn't reassign innerHTML) on
  // every render — same reasoning as the timeline's SafeHtml wrapper.
  const requestHtml = gate?.requestBodyHtml ?? null;
  const requestDanger = React.useMemo(
    () => (requestHtml ? { __html: DOMPurify.sanitize(requestHtml) } : null),
    [requestHtml],
  );

  if (!gate) return null;

  const showRequestPanel =
    gate.showRequest && (!!requestDanger || !!gate.requestBodyText);

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
        onEscapeKeyDown={(e) => {
          // The gate never closes on a plain Esc. Admins get a Shift+Esc
          // silent escape hatch (close without running any confirm actions).
          e.preventDefault();
          if (adminCanDismiss && e.shiftKey) onDismiss!();
        }}
        onInteractOutside={(e) => e.preventDefault()}
        onPointerDownOutside={(e) => e.preventDefault()}
      >
        <DialogHeader>
          <DialogTitle>{gate.title}</DialogTitle>
          <DialogDescription
            className={
              gate.message
                ? "whitespace-pre-wrap text-sm text-muted-foreground"
                : "sr-only"
            }
          >
            {gate.message || "Review the ticket title before continuing."}
          </DialogDescription>
        </DialogHeader>

        {showRequestPanel && (
          <div className="space-y-1.5">
            <div className="flex items-center justify-between gap-2">
              <span className="text-xs font-medium text-muted-foreground">
                {gate.requestSource === "mail"
                  ? "Original request — from the first email"
                  : "Original request"}
              </span>
              {gate.requestEmlUrl ? (
                <a
                  href={gate.requestEmlUrl}
                  target="_blank"
                  rel="noreferrer"
                  className="inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
                  title="Download the original email, including images and attachments"
                >
                  <Download className="h-3 w-3" />
                  .eml
                </a>
              ) : null}
            </div>
            <div className="relative max-h-48 overflow-y-auto rounded-md border border-glass bg-glass px-3 py-2">
              {requestDanger ? (
                <div
                  className="prose-sm text-sm text-foreground/90 [&_a]:text-primary [&_a]:underline [&_p]:my-1 [&_ul]:list-disc [&_ul]:list-outside [&_ul]:pl-6 [&_ol]:list-decimal [&_ol]:list-outside [&_ol]:pl-6 [&_img]:max-w-full"
                  dangerouslySetInnerHTML={requestDanger}
                />
              ) : (
                <p className="whitespace-pre-wrap text-sm text-foreground/90">
                  {gate.requestBodyText}
                </p>
              )}
            </div>
          </div>
        )}

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

        {adminCanDismiss && (
          <p className="text-[11px] leading-snug text-muted-foreground/60">
            Admin: hold <span className="font-medium text-muted-foreground/80">Shift</span> and click
            Dismiss (or press{" "}
            <span className="font-medium text-muted-foreground/80">Shift+Esc</span>) to close this
            review without logging it.
          </p>
        )}

        <DialogFooter className="gap-2 sm:gap-2">
          <div
            className={cn(
              "flex w-full gap-2",
              adminCanDismiss
                ? "flex-col-reverse sm:flex-row sm:items-center sm:justify-between"
                : "justify-end",
            )}
          >
            {adminCanDismiss && (
              <Button
                variant="ghost"
                onClick={() => onDismiss!()}
                disabled={!shiftHeld}
                className="text-muted-foreground hover:text-foreground"
                title="Admins only — close the review without logging it"
              >
                {shiftHeld ? "Dismiss (no log)" : "Hold Shift to dismiss"}
              </Button>
            )}
            <Button
              onClick={handleConfirm}
              disabled={!canConfirm}
              className="bg-gradient-to-r from-violet-600 to-indigo-600 hover:from-violet-500 hover:to-indigo-500 text-white shadow-[0_0_20px_rgba(124,58,237,0.3)]"
            >
              {gate.confirmLabel}
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
