import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Check, Clock } from "lucide-react";
import { toast } from "sonner";

import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";
import { ApiError } from "@/lib/ticket-api";
import { timesheetTicketApi, formatDuration } from "@/lib/timesheet-api";

/// Shared query key for the per-ticket hour-limit snapshot so the dialog and
/// the "Time logged" panel share one cached fetch. The queue is part of the
/// key: a queue change re-resolves the alert (different queue → different
/// limit / on-off), so the snapshot must refetch when the queue changes.
export const timeAlertQueryKey = (ticketId: string, queueId: string | null | undefined) =>
  ["timesheet", "ticket-alert", ticketId, queueId ?? "none"] as const;

type Props = {
  ticketId: string;
  /// The ticket's current queue. Drives re-evaluation on queue change.
  queueId: string | null | undefined;
};

/// v0.0.87 — per-ticket hour-limit warning. Mounted on the ticket-detail
/// page; when the feature is on and the ticket is over its effective limit,
/// it opens a modal the agent must act on. The agent either dismisses it
/// (logged; recurs next time the ticket is opened) or raises the ticket's
/// limit (requires the written-customer-confirmation tick). All limit logic
/// is server-side — this component only renders the server snapshot.
export function TicketTimeAlertDialog({ ticketId, queueId }: Props) {
  const queryClient = useQueryClient();

  // Reset the "already handled in this view" guard whenever we navigate to a
  // different ticket OR the ticket's queue changes, so a queue change re-warns
  // under the new queue's rule.
  const [handled, setHandled] = React.useState(false);
  React.useEffect(() => {
    setHandled(false);
    setExtendMode(false);
    setConfirmed(false);
  }, [ticketId, queueId]);

  const [extendMode, setExtendMode] = React.useState(false);
  const [confirmed, setConfirmed] = React.useState(false);
  const [extraMinutes, setExtraMinutes] = React.useState<string>("");

  const statusQ = useQuery({
    queryKey: timeAlertQueryKey(ticketId, queueId),
    queryFn: () => timesheetTicketApi.timeAlert(ticketId),
    staleTime: 15_000,
  });
  const status = statusQ.data;

  // Pre-fill the minutes input from the server default the first time the
  // extend form is revealed.
  React.useEffect(() => {
    if (extendMode && status && extraMinutes === "") {
      setExtraMinutes(String(status.defaultExtraMinutes));
    }
  }, [extendMode, status, extraMinutes]);

  const open = Boolean(status?.enabled && status?.exceeded && !handled);

  const invalidate = () => {
    // Prefix match covers every queue variant of the alert key.
    queryClient.invalidateQueries({ queryKey: ["timesheet", "ticket-alert", ticketId] });
    queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
    queryClient.invalidateQueries({ queryKey: ["timesheet", "ticket", ticketId] });
  };

  const dismiss = useMutation({
    mutationFn: () => timesheetTicketApi.dismissTimeAlert(ticketId),
    onSuccess: () => {
      setHandled(true);
      invalidate();
    },
    onError: (e) =>
      toast.error(
        e instanceof ApiError ? e.message : "Could not dismiss the warning.",
      ),
  });

  const extend = useMutation({
    mutationFn: (minutes: number) =>
      timesheetTicketApi.extendTimeAlert(ticketId, {
        addMinutes: minutes,
        customerConfirmed: true,
      }),
    onSuccess: (_data, minutes) => {
      setHandled(true);
      invalidate();
      toast.success(`Ticket limit raised by ${formatDuration(minutes)}.`);
    },
    onError: (e) =>
      toast.error(
        e instanceof ApiError ? e.message : "Could not raise the limit.",
      ),
  });

  const parsedMinutes = Number.parseInt(extraMinutes, 10);
  const minutesValid = Number.isFinite(parsedMinutes) && parsedMinutes > 0;
  const busy = dismiss.isPending || extend.isPending;

  if (!status) return null;

  const overBy = Math.max(0, status.totalMinutes - status.limitMinutes);

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        // The agent must make a choice — closing via overlay/Esc counts as a
        // dismissal so we don't silently swallow the warning.
        if (!next && open && !busy) dismiss.mutate();
      }}
    >
      <DialogContent className="glass-panel border-amber-400/30 sm:max-w-md">
        <DialogHeader>
          <div className="mx-auto mb-1 flex h-11 w-11 items-center justify-center rounded-full bg-amber-400/15 sm:mx-0">
            <AlertTriangle className="h-5 w-5 text-amber-300" />
          </div>
          <DialogTitle>Hour limit reached on this ticket</DialogTitle>
          <DialogDescription>
            More than {formatDuration(status.limitMinutes)} has been logged on
            this ticket ({formatDuration(status.totalMinutes)} total
            {overBy > 0 ? `, ${formatDuration(overBy)} over the limit` : ""}).
          </DialogDescription>
        </DialogHeader>

        {extendMode && (
          <div className="space-y-3 rounded-lg border border-glass bg-glass/40 p-3">
            <div className="flex items-center gap-2">
              <Clock className="h-4 w-4 shrink-0 text-violet-300/80" />
              <span className="text-sm text-muted-foreground">
                Add extra time to this ticket's limit:
              </span>
            </div>
            <div className="flex items-center gap-2">
              <Input
                type="number"
                min={1}
                value={extraMinutes}
                onChange={(e) => setExtraMinutes(e.target.value)}
                className="h-9 w-28 font-mono"
                disabled={busy}
                aria-label="Extra minutes to add"
              />
              <span className="text-xs text-muted-foreground">
                minutes
                {minutesValid && (
                  <span className="ml-1 text-muted-foreground/70">
                    (= {formatDuration(parsedMinutes)})
                  </span>
                )}
              </span>
            </div>

            <label className="flex cursor-pointer items-start gap-2.5 text-sm">
              <button
                type="button"
                role="checkbox"
                aria-checked={confirmed}
                onClick={() => setConfirmed((v) => !v)}
                disabled={busy}
                className={cn(
                  "mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded border transition-colors",
                  confirmed
                    ? "border-violet-400 bg-violet-500/80 text-white"
                    : "border-glass-strong bg-glass",
                )}
              >
                {confirmed && <Check className="h-3 w-3" />}
              </button>
              <span className="text-muted-foreground">
                {status.confirmationText}
              </span>
            </label>
          </div>
        )}

        <DialogFooter className="gap-2 sm:gap-2">
          {!extendMode ? (
            <>
              <Button
                variant="ghost"
                onClick={() => dismiss.mutate()}
                disabled={busy}
              >
                Cancel
              </Button>
              <Button onClick={() => setExtendMode(true)} disabled={busy}>
                Allow more time…
              </Button>
            </>
          ) : (
            <>
              <Button
                variant="ghost"
                onClick={() => setExtendMode(false)}
                disabled={busy}
              >
                Back
              </Button>
              <Button
                onClick={() => extend.mutate(parsedMinutes)}
                disabled={busy || !confirmed || !minutesValid}
              >
                Confirm &amp; raise limit
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
