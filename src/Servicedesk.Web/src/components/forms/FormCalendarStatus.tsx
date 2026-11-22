import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { CalendarClock, ExternalLink, RefreshCw, Video } from "lucide-react";
import { Button } from "@/components/ui/button";
import { calendarApi, type FormCalendarEvent } from "@/lib/forms-api";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";

/// v0.1.61 — the form's Outlook appointments in the read view: one compact
/// row per appointment with a single status word, "Open in Outlook" / "Join"
/// links and, when something could not be written, one "Retry" for the form.
/// Renders nothing for forms that never planned an appointment.
const LABEL: Record<FormCalendarEvent["status"], string> = {
  Pending: "Being written to Outlook…",
  Synced: "In Outlook",
  Failed: "Not synced",
  CancelPending: "Being cancelled…",
  Cancelled: "Cancelled",
};

export function FormCalendarStatus({ formId, ticketId }: { formId: string; ticketId: string }) {
  const qc = useQueryClient();
  const key = ["ticket", ticketId, "forms", formId, "calendar"] as const;
  const status = useQuery({
    queryKey: key,
    queryFn: () => calendarApi.status(formId),
    staleTime: 10_000,
    // While something is still on its way, look again shortly.
    refetchInterval: (q) => ((q.state.data ?? []).some((e) => e.status === "Pending" || e.status === "CancelPending") ? 5_000 : false),
  });
  const retry = useMutation({
    mutationFn: () => calendarApi.retry(formId),
    onSuccess: () => {
      toast.success("Trying again");
      void qc.invalidateQueries({ queryKey: key });
    },
    onError: (err) => toast.error(errorMessage(err, "Could not retry.")),
  });

  const rows = (status.data ?? []).filter((e) => e.status !== "Cancelled" || e.webLink);
  if (rows.length === 0) return null;
  const failed = rows.filter((e) => e.status === "Failed");

  return (
    <section className="sd-form-calendar mb-4 rounded-lg border border-glass-strong bg-glass px-4 py-3">
      <div className="flex items-center gap-2">
        <CalendarClock className="h-4 w-4 text-primary" />
        <span className="text-xs font-medium text-foreground">Outlook</span>
        {failed.length > 0 && (
          <Button variant="ghost" size="sm" className="ml-auto h-7 px-2 text-xs" disabled={retry.isPending} onClick={() => retry.mutate()}>
            <RefreshCw className={cn("mr-1 h-3.5 w-3.5", retry.isPending && "animate-spin")} /> Retry
          </Button>
        )}
      </div>
      <ul className="mt-2 space-y-1.5">
        {rows.map((e) => (
          <li key={e.id} className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-xs">
            <span className="font-medium text-foreground">{e.userName}</span>
            {e.isTeams && <span className="text-muted-foreground">· Teams organizer</span>}
            <span className={cn(
              "rounded-md border px-1.5 py-px text-[10px] font-medium",
              e.status === "Synced" ? "border-glass-strong text-muted-foreground"
                : e.status === "Failed" ? "border-destructive/40 bg-destructive/10 text-destructive"
                : "border-glass-strong text-muted-foreground",
            )}>
              {LABEL[e.status]}
            </span>
            {e.webLink && e.status === "Synced" && (
              <a href={e.webLink} target="_blank" rel="noreferrer noopener"
                className="inline-flex items-center gap-1 text-primary underline-offset-2 hover:underline">
                Open in Outlook <ExternalLink className="h-3 w-3" />
              </a>
            )}
            {e.joinUrl && e.status === "Synced" && (
              <a href={e.joinUrl} target="_blank" rel="noreferrer noopener"
                className="inline-flex items-center gap-1 text-primary underline-offset-2 hover:underline">
                <Video className="h-3 w-3" /> Join Teams meeting
              </a>
            )}
            {e.status === "Failed" && e.lastError && (
              <span className="basis-full text-muted-foreground">{e.lastError}</span>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}
