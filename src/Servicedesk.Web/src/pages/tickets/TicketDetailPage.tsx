import * as React from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowLeft } from "lucide-react";
import DOMPurify from "dompurify";
import { ticketApi, type TicketFieldUpdate } from "@/lib/ticket-api";
import { Skeleton } from "@/components/ui/skeleton";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";
import { TicketSidePanel } from "./components/TicketSidePanel";
import { TicketTimeline } from "./components/TicketTimeline";
import { AddNoteForm } from "./components/AddNoteForm";

type TicketDetailPageProps = {
  ticketId: string;
};

function TicketBodyHtml({ html, text }: { html: string | null; text: string }) {
  const sanitized = html
    ? DOMPurify.sanitize(html)
    : DOMPurify.sanitize(text);

  return (
    <div
      className="prose-sm text-foreground/90 [&_a]:text-primary [&_a]:underline [&_p]:my-1.5 [&_ul]:pl-5 [&_ol]:pl-5 [&_blockquote]:border-l-2 [&_blockquote]:border-white/20 [&_blockquote]:pl-3 [&_blockquote]:text-muted-foreground"
      dangerouslySetInnerHTML={{ __html: sanitized }}
    />
  );
}

function LoadingSkeleton() {
  return (
    <div className="flex gap-6 animate-pulse">
      <div className="flex-1 space-y-4">
        <Skeleton className="h-8 w-2/3" />
        <Skeleton className="h-4 w-1/4" />
        <div className="glass-card p-6 space-y-3">
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-5/6" />
          <Skeleton className="h-4 w-4/6" />
        </div>
        <Skeleton className="h-6 w-32 mt-6" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
      <div className="w-[320px] shrink-0 space-y-4">
        <Skeleton className="h-[480px] w-full rounded-[var(--radius)]" />
      </div>
    </div>
  );
}

export function TicketDetailPage({ ticketId }: TicketDetailPageProps) {
  const queryClient = useQueryClient();
  const addTicket = useRecentTicketsStore((s) => s.addTicket);

  const { data, isLoading, isError } = useQuery({
    queryKey: ["ticket", ticketId],
    queryFn: () => ticketApi.get(ticketId),
  });

  React.useEffect(() => {
    if (data?.ticket) {
      addTicket({
        id: data.ticket.id,
        number: data.ticket.number,
        subject: data.ticket.subject,
      });
    }
  }, [data?.ticket, addTicket]);

  const updateMutation = useMutation({
    mutationFn: (fields: TicketFieldUpdate) => ticketApi.update(ticketId, fields),
    onSuccess: (updated) => {
      queryClient.setQueryData(["ticket", ticketId], updated);
      toast.success("Ticket updated");
    },
    onError: () => toast.error("Failed to update ticket"),
  });

  if (isLoading) {
    return <LoadingSkeleton />;
  }

  if (isError || !data) {
    return (
      <div className="flex flex-col items-center justify-center py-24 gap-3">
        <div className="text-lg font-medium text-foreground/70">
          Ticket not found
        </div>
        <div className="text-sm text-muted-foreground">
          This ticket does not exist or you do not have access.
        </div>
      </div>
    );
  }

  const { ticket, body, events } = data;

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-center gap-2">
        <a
          href="/tickets"
          className="flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors"
        >
          <ArrowLeft className="h-3.5 w-3.5" />
          Back to tickets
        </a>
      </div>

      <div className="flex gap-6 items-start">
        <div className="flex-1 min-w-0 space-y-6">
          <div>
            <div className="flex items-center gap-2 mb-1">
              <span className="text-xs font-mono font-medium px-2 py-0.5 rounded border border-white/10 bg-white/[0.05] text-muted-foreground">
                #{ticket.number}
              </span>
            </div>
            <h1 className="text-2xl font-semibold tracking-tight text-foreground leading-tight">
              {ticket.subject}
            </h1>
          </div>

          <div className="glass-card p-6">
            <TicketBodyHtml html={body.bodyHtml} text={body.bodyText} />
          </div>

          <div>
            <div className="flex items-center gap-3 mb-4">
              <div className="h-px flex-1 bg-white/10" />
              <span className="text-xs uppercase tracking-wider text-muted-foreground">
                Activity
              </span>
              <div className="h-px flex-1 bg-white/10" />
            </div>

            <TicketTimeline events={events} />
          </div>

          <AddNoteForm
            ticketId={ticketId}
            onSubmitted={() => {
              queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
            }}
          />
        </div>

        <TicketSidePanel
          ticket={ticket}
          onUpdate={async (fields) => { await updateMutation.mutateAsync(fields); }}
        />
      </div>
    </div>
  );
}
