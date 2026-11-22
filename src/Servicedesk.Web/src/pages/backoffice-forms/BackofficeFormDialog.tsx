import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouterState } from "@tanstack/react-router";
import { toast } from "sonner";
import { ArrowUpRight, CheckCircle2, ClipboardList, History, RotateCcw } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { useConfirm } from "@/components/ConfirmDialog";
import { FormAnswersView } from "@/components/forms/FormDialogs";
import { formInputCn } from "@/components/forms/FormFieldInput";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";
import {
  BACKOFFICE_FORMS_KEY,
  backofficeFormKeys,
  backofficeFormsApi,
  type BackofficeFormItem,
  type BackofficeFormLogEntry,
} from "@/lib/backoffice-forms-api";
import { useTicketOverlayStore } from "@/stores/useTicketOverlayStore";
import { BackofficeStatusChip } from "./BackofficeStatusChip";
import { presenceName } from "./backofficeLabels";

const LOG_LABEL: Record<BackofficeFormLogEntry["action"], string> = {
  submitted: "Submitted",
  claimed: "Started handling",
  released: "Released",
  handled: "Marked as handled",
  reopened: "Reopened",
  updated: "Form edited",
};

/// One back-office form, read-only, with the two back-office steps: Mark as
/// handled (with a note) and Reopen. Every step is a guarded server
/// transition; a 409 (someone else was quicker, or the form changed) shows
/// the message and refreshes the dialog with the real state.
/// v0.1.61 — no claim step; "Open ticket" opens the ticket overlay.
export function BackofficeFormDialog({
  formId,
  onOpenChange,
  startInHandle = false,
  hideOpenTicket = false,
  onHandled,
}: {
  formId: string | null;
  onOpenChange: (open: boolean) => void;
  /** Open straight in the "Mark as handled" step (overlay primary action). */
  startInHandle?: boolean;
  /** Inside the ticket overlay the ticket is already open. */
  hideOpenTicket?: boolean;
  /** Called after the form was marked as handled (the overlay closes its ticket). */
  onHandled?: () => void;
}) {
  const open = formId !== null;
  const qc = useQueryClient();
  const zone = useServerTimeZone();
  const confirm = useConfirm();
  const openOverlay = useTicketOverlayStore((s) => s.open);
  const location = useRouterState({ select: (s) => s.location });
  const [handling, setHandling] = React.useState(false);
  const [note, setNote] = React.useState("");
  const [showLog, setShowLog] = React.useState(false);

  React.useEffect(() => {
    setNote("");
    setShowLog(false);
    setHandling(startInHandle);
  }, [formId, startInHandle]);

  const detail = useQuery({
    queryKey: backofficeFormKeys.detail(formId ?? ""),
    queryFn: () => backofficeFormsApi.get(formId!),
    enabled: open,
    retry: false,
  });

  const refresh = () => void qc.invalidateQueries({ queryKey: BACKOFFICE_FORMS_KEY });
  const act = useMutation({
    mutationFn: (fn: () => Promise<BackofficeFormItem>) => fn(),
    onSuccess: refresh,
    onError: (err) => {
      toast.error(errorMessage(err, "The form could not be updated."));
      refresh();
    },
  });

  const data = detail.data;
  const item = data?.item;

  const handle = () => {
    if (!item) return;
    act.mutate(async () => {
      const r = await backofficeFormsApi.handle(item.id, note, item.version);
      toast.success("Form marked as handled");
      setNote("");
      setHandling(false);
      onOpenChange(false);
      onHandled?.();
      return r;
    });
  };
  const reopen = async () => {
    if (!item) return;
    const ok = await confirm({
      title: "Reopen this form?",
      description: "It goes back to Open on the back office list.",
      confirmLabel: "Reopen",
    });
    if (ok) act.mutate(() => backofficeFormsApi.reopen(item.id, ""));
  };

  const openTicket = () => {
    if (!item) return;
    onOpenChange(false);
    openOverlay({
      ticketId: item.ticketId,
      ticketNumber: item.ticketNumber,
      subject: item.ticketSubject,
      source: { path: location.pathname, search: location.searchStr, tab: null },
    });
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sd-form-dialog sd-bo-form-dialog flex max-h-[90vh] w-[95vw] max-w-3xl flex-col gap-0 p-0">
        <DialogHeader className="border-b border-glass px-6 pb-4 pt-5">
          <div className="flex items-start gap-3">
            <span className="sd-form-icon mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border border-primary/25 bg-primary/10 text-primary">
              <ClipboardList className="h-4 w-4" />
            </span>
            <div className="min-w-0 flex-1">
              <DialogTitle className="flex flex-wrap items-center gap-2 text-lg">
                <span className="truncate">{item?.name ?? "Form"}</span>
                {item && <BackofficeStatusChip item={{ status: item.status, handledByName: item.handledByName }} />}
              </DialogTitle>
              <DialogDescription className="mt-0.5">
                {item
                  ? `Submitted by ${item.submittedByName ? presenceName(item.submittedByName) : "unknown"} · ${formatDateTime(item.submittedUtc, zone)}`
                  : "Loading…"}
              </DialogDescription>
              {item && !hideOpenTicket && (
                <button type="button" onClick={openTicket}
                  className="mt-2 inline-flex max-w-full items-center gap-1.5 rounded-md border border-glass-strong bg-glass px-2.5 py-1 text-xs text-foreground transition-colors hover:bg-glass-hover">
                  <span className="tabular-nums text-muted-foreground">#{item.ticketNumber}</span>
                  <span className="truncate">{item.ticketSubject}</span>
                  {item.companyName && <span className="truncate text-muted-foreground">· {item.companyName}</span>}
                  <ArrowUpRight className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                </button>
              )}
            </div>
          </div>
        </DialogHeader>

        <div className="min-h-0 flex-1 overflow-y-auto px-6 py-5">
          {detail.isLoading ? (
            <div className="space-y-4">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-2/3" />
            </div>
          ) : detail.isError || !data || !item ? (
            <p className="py-6 text-sm text-destructive">
              This form could not be loaded. It may have been removed from its ticket.
            </p>
          ) : (
            <div className="space-y-5">
              {item.reopened && data.changesSinceHandled.length > 0 && (
                <section className="rounded-lg border border-amber-400/40 bg-amber-100/60 px-4 py-3 text-amber-900 dark:bg-amber-400/10 dark:text-amber-100">
                  <p className="text-xs font-medium">
                    Changed after it was handled{item.updatedByName ? ` by ${presenceName(item.updatedByName)}` : ""}
                  </p>
                  <ChangesTable changes={data.changesSinceHandled} />
                </section>
              )}
              {item.status === "Handled" && (
                <section className="rounded-lg border border-glass-strong bg-glass px-4 py-3">
                  <p className="flex items-center gap-1.5 text-xs font-medium text-foreground">
                    <CheckCircle2 className="h-3.5 w-3.5 text-primary" />
                    Handled by {item.handledByName ? presenceName(item.handledByName) : "a trigger"}
                    {item.handledUtc && <span className="font-normal text-muted-foreground">· {formatDateTime(item.handledUtc, zone)}</span>}
                  </p>
                  {item.handledNote && <p className="mt-1.5 whitespace-pre-wrap text-sm text-foreground">{item.handledNote}</p>}
                </section>
              )}

              <FormAnswersView fields={data.fields} answers={data.answers} />

              <section>
                <button type="button" onClick={() => setShowLog((s) => !s)}
                  className="inline-flex items-center gap-1.5 text-xs text-muted-foreground transition-colors hover:text-foreground">
                  <History className="h-3.5 w-3.5" />
                  {showLog ? "Hide history" : `History (${data.log.length})`}
                </button>
                {showLog && (
                  <ol className="mt-2 space-y-1.5 border-l border-glass pl-4">
                    {data.log.map((l) => (
                      <li key={l.id} className="text-xs">
                        <span className="font-medium text-foreground">{LOG_LABEL[l.action] ?? l.action}</span>
                        <span className="text-muted-foreground"> · {l.userName ? presenceName(l.userName) : "trigger"} · {formatDateTime(l.createdUtc, zone)} · v{l.formVersion}</span>
                        {l.note && <p className="mt-0.5 whitespace-pre-wrap text-muted-foreground">{l.note}</p>}
                      </li>
                    ))}
                  </ol>
                )}
              </section>
            </div>
          )}
        </div>

        {item && (
          <div className="border-t border-glass px-6 py-3.5">
            {handling && item.status === "Open" && (
              <textarea autoFocus rows={3} maxLength={4000} value={note} onChange={(e) => setNote(e.target.value)}
                placeholder="What was done — e.g. licences activated, contract updated in the CRM"
                className={cn(formInputCn, "mb-3 min-h-20 resize-y py-2")} />
            )}
            <div className="flex items-center justify-end gap-2">
              {handling && item.status === "Open" ? (
                <>
                  <Button variant="ghost" size="sm" onClick={() => setHandling(false)}>Back</Button>
                  <Button size="sm" disabled={act.isPending} onClick={handle}>
                    <CheckCircle2 className="mr-1.5 h-3.5 w-3.5" /> Confirm handled
                  </Button>
                </>
              ) : (
                <>
                  <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)}>Close</Button>
                  {item.status === "Open" && (
                    <Button size="sm" disabled={act.isPending} onClick={() => setHandling(true)}>
                      <CheckCircle2 className="mr-1.5 h-3.5 w-3.5" /> Mark as handled
                    </Button>
                  )}
                  {item.status === "Handled" && (
                    <Button size="sm" variant="secondary" disabled={act.isPending} onClick={() => void reopen()}>
                      <RotateCcw className="mr-1.5 h-3.5 w-3.5" /> Reopen
                    </Button>
                  )}
                </>
              )}
            </div>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

function ChangesTable({ changes }: { changes: { fieldId: string; label: string; before: string; after: string }[] }) {
  return (
    <table className="mt-2 w-full text-xs">
      <tbody className="divide-y divide-amber-400/25">
        {changes.map((c) => (
          <tr key={c.fieldId}>
            <td className="py-1.5 pr-3 font-medium">{c.label}</td>
            <td className="whitespace-pre-wrap py-1.5 pr-2 text-amber-900/70 line-through decoration-amber-700/40 dark:text-amber-100/60">{c.before || "—"}</td>
            <td className="whitespace-pre-wrap py-1.5">{c.after || "—"}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
