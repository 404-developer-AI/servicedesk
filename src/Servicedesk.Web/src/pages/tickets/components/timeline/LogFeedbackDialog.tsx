import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { serverTodayNow } from "@/lib/serverToday";
import { apiFieldErrors } from "@/lib/http";
import { ApiError } from "@/lib/ticket-api";
import { feedbackApi, type FeedbackEmployee, type FeedbackWorkPointType } from "@/lib/feedback-api";
import { Button } from "@/components/ui/button";
import { RichTextEditor } from "@/components/RichTextEditor";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

// ---- Log employee feedback (from a timeline item) -------------------------

const FEEDBACK_SELECT_CLASS =
  "h-9 w-full rounded-md border border-glass bg-glass px-2 text-sm text-foreground focus:outline-hidden focus:ring-2 focus:ring-ring";

export function LogFeedbackDialog({
  open,
  onOpenChange,
  ticketId,
  ticketNumber,
  eventId,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  ticketId: string;
  ticketNumber: number;
  eventId: number;
}) {
  const queryClient = useQueryClient();
  // Read once when needed — a ticking subscription here re-rendered every
  // timeline event each second.
  const [targetUserId, setTargetUserId] = React.useState("");
  const [entryDate, setEntryDate] = React.useState(() => serverTodayNow());
  const [workPointTypeId, setWorkPointTypeId] = React.useState("");
  const [bodyHtml, setBodyHtml] = React.useState("");
  const [fieldErrors, setFieldErrors] = React.useState<Record<string, string>>({});

  // Reset the form each time the dialog opens (date back to today).
  React.useEffect(() => {
    if (open) {
      setTargetUserId("");
      setEntryDate(serverTodayNow());
      setWorkPointTypeId("");
      setBodyHtml("");
      setFieldErrors({});
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const employeesQuery = useQuery({
    queryKey: ["feedback", "employees"],
    queryFn: feedbackApi.listEmployees,
    enabled: open,
    staleTime: 60_000,
  });
  const typesQuery = useQuery({
    queryKey: ["feedback", "work-point-types"],
    queryFn: feedbackApi.listWorkPointTypes,
    enabled: open,
    staleTime: 60_000,
  });
  const employees: FeedbackEmployee[] = employeesQuery.data?.items ?? [];
  const types: FeedbackWorkPointType[] = typesQuery.data?.items ?? [];

  const saveMutation = useMutation({
    mutationFn: () =>
      feedbackApi.logEntry({
        targetUserId,
        entryDate,
        workPointTypeId: workPointTypeId || null,
        bodyHtml: bodyHtml || null,
        linkedTicketId: ticketId,
        linkedTicketNumber: ticketNumber,
        linkedTicketEventId: eventId,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["feedback", "entries"] });
      queryClient.invalidateQueries({ queryKey: ["feedback", "logged-events", ticketId] });
      toast.success("Feedback logged to the employee feedback board.");
      onOpenChange(false);
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 422) {
        setFieldErrors(apiFieldErrors(err) ?? {});
        toast.error("Please fix the validation errors.");
      } else {
        toast.error("Could not log feedback.");
      }
    },
  });

  const canSave = targetUserId !== "" && !saveMutation.isPending;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Log employee feedback</DialogTitle>
          <DialogDescription className="sr-only">
            Log employee feedback linked to this ticket event.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div className="text-xs text-muted-foreground">
            Linked to ticket #{ticketNumber} · this note/reply/email.
          </div>
          <div className="space-y-1">
            <label className="text-[10px] font-medium uppercase tracking-widest text-muted-foreground/70">
              Employee
            </label>
            <select
              value={targetUserId}
              onChange={(e) => setTargetUserId(e.target.value)}
              className={FEEDBACK_SELECT_CLASS}
            >
              <option value="">— Select an employee —</option>
              {employees.map((emp) => (
                <option key={emp.id} value={emp.id}>{emp.email}</option>
              ))}
            </select>
            {fieldErrors.targetUserId && (
              <span className="text-xs text-destructive">{fieldErrors.targetUserId}</span>
            )}
          </div>
          <div className="flex gap-3">
            <div className="flex-1 space-y-1">
              <label className="text-[10px] font-medium uppercase tracking-widest text-muted-foreground/70">
                Date
              </label>
              <input
                type="date"
                value={entryDate}
                onChange={(e) => setEntryDate(e.target.value)}
                className={FEEDBACK_SELECT_CLASS}
              />
            </div>
            <div className="flex-1 space-y-1">
              <label className="text-[10px] font-medium uppercase tracking-widest text-muted-foreground/70">
                Work-point type
              </label>
              <select
                value={workPointTypeId}
                onChange={(e) => setWorkPointTypeId(e.target.value)}
                className={FEEDBACK_SELECT_CLASS}
              >
                <option value="">— None —</option>
                {types.map((t) => (
                  <option key={t.id} value={t.id}>{t.name}</option>
                ))}
              </select>
            </div>
          </div>
          <div className="space-y-1">
            <label className="text-[10px] font-medium uppercase tracking-widest text-muted-foreground/70">
              Feedback
            </label>
            <RichTextEditor
              content={bodyHtml}
              onChange={setBodyHtml}
              placeholder="Describe the point of attention or success…"
              minHeight="140px"
              maxHeight="320px"
            />
            {fieldErrors.bodyHtml && (
              <span className="text-xs text-destructive">{fieldErrors.bodyHtml}</span>
            )}
          </div>
          <div className="flex justify-end gap-2 pt-1">
            <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button size="sm" disabled={!canSave} onClick={() => saveMutation.mutate()}>
              Log feedback
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}
