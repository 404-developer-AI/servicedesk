import * as React from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowLeft, Check, Copy, Pencil, X } from "lucide-react";
import { ticketApi, type TicketFieldUpdate } from "@/lib/ticket-api";
import { Skeleton } from "@/components/ui/skeleton";
import { RichTextEditor } from "@/components/RichTextEditor";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";
import { useViewingTicket } from "@/hooks/usePresence";
import { TicketSidePanel } from "./components/TicketSidePanel";
import { TicketTimeline } from "./components/TicketTimeline";
import { AddNoteForm } from "./components/AddNoteForm";
import { cn } from "@/lib/utils";

type TicketDetailPageProps = {
  ticketId: string;
};

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

/* ─── Click-to-copy ticket number ─── */

function TicketNumber({ number }: { number: number }) {
  const [copied, setCopied] = React.useState(false);

  const handleCopy = async () => {
    await navigator.clipboard.writeText(`#${number}`);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  return (
    <button
      type="button"
      onClick={handleCopy}
      title="Click to copy"
      className="group flex items-center gap-1 text-xs font-mono font-medium px-2 py-0.5 rounded border border-white/10 bg-white/[0.05] text-muted-foreground hover:bg-white/[0.08] transition-colors"
    >
      #{number}
      {copied ? (
        <Check className="h-3 w-3 text-green-400" />
      ) : (
        <Copy className="h-3 w-3 opacity-0 group-hover:opacity-60 transition-opacity" />
      )}
    </button>
  );
}

/* ─── Editable subject ─── */

function EditableSubject({
  value,
  onSave,
}: {
  value: string;
  onSave: (subject: string) => Promise<void>;
}) {
  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState(value);
  const inputRef = React.useRef<HTMLInputElement>(null);

  React.useEffect(() => {
    setDraft(value);
  }, [value]);

  React.useEffect(() => {
    if (editing) inputRef.current?.focus();
  }, [editing]);

  const save = async () => {
    const trimmed = draft.trim();
    if (!trimmed || trimmed === value) {
      setDraft(value);
      setEditing(false);
      return;
    }
    await onSave(trimmed);
    setEditing(false);
  };

  const cancel = () => {
    setDraft(value);
    setEditing(false);
  };

  if (!editing) {
    return (
      <div className="group flex items-start gap-2">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground leading-tight flex-1 min-w-0">
          {value}
        </h1>
        <button
          type="button"
          onClick={() => setEditing(true)}
          className="shrink-0 mt-1 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-white/[0.06] transition-all"
          title="Edit subject"
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
      </div>
    );
  }

  return (
    <div className="flex items-center gap-2">
      <input
        ref={inputRef}
        type="text"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") save();
          if (e.key === "Escape") cancel();
        }}
        className="flex-1 min-w-0 text-2xl font-semibold tracking-tight text-foreground leading-tight bg-transparent border-b-2 border-primary/60 outline-none py-0.5"
      />
      <button
        type="button"
        onClick={save}
        className="shrink-0 p-1.5 rounded-md text-green-400 hover:bg-green-400/10 transition-colors"
        title="Save"
      >
        <Check className="h-4 w-4" />
      </button>
      <button
        type="button"
        onClick={cancel}
        className="shrink-0 p-1.5 rounded-md text-muted-foreground hover:bg-white/[0.06] transition-colors"
        title="Cancel"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}

/* ─── Editable description ─── */

function EditableDescription({
  html,
  text,
  onSave,
}: {
  html: string | null;
  text: string;
  onSave: (bodyHtml: string, bodyText: string) => Promise<void>;
}) {
  const [editing, setEditing] = React.useState(false);
  const [draftHtml, setDraftHtml] = React.useState(html ?? text);

  React.useEffect(() => {
    setDraftHtml(html ?? text);
  }, [html, text]);

  const save = async () => {
    // Strip tags for plain text fallback
    const div = document.createElement("div");
    div.innerHTML = draftHtml;
    const plainText = div.textContent ?? "";
    await onSave(draftHtml, plainText);
    setEditing(false);
  };

  const cancel = () => {
    setDraftHtml(html ?? text);
    setEditing(false);
  };

  if (!editing) {
    return (
      <div
        className="group relative glass-card p-6 cursor-pointer hover:border-white/15 transition-colors"
        onClick={() => setEditing(true)}
        title="Click to edit description"
      >
        <button
          type="button"
          className="absolute top-3 right-3 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-white/[0.06] transition-all"
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
        <RichTextEditor
          content={html ?? text}
          editable={false}
          minHeight="60px"
          className="border-none bg-transparent !rounded-none"
        />
      </div>
    );
  }

  return (
    <div className="glass-card p-4 space-y-3">
      <RichTextEditor
        content={draftHtml}
        onChange={setDraftHtml}
        placeholder="Describe the issue..."
        minHeight="100px"
      />
      <div className="flex items-center gap-2 justify-end">
        <button
          type="button"
          onClick={cancel}
          className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-white/[0.06] transition-colors"
        >
          Cancel
        </button>
        <button
          type="button"
          onClick={save}
          className="px-3 py-1.5 text-xs rounded-md bg-primary text-white hover:bg-primary/90 transition-colors"
        >
          Save
        </button>
      </div>
    </div>
  );
}

/* ─── Main page ─── */

export function TicketDetailPage({ ticketId }: TicketDetailPageProps) {
  const queryClient = useQueryClient();
  const addTicket = useRecentTicketsStore((s) => s.addTicket);
  useViewingTicket(ticketId);

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

      <div className="flex gap-6 items-start relative">
        <div className="flex-1 min-w-0 space-y-6 overflow-y-auto">
          {/* Ticket number + subject */}
          <div>
            <div className="flex items-center gap-2 mb-1">
              <TicketNumber number={ticket.number} />
            </div>
            <EditableSubject
              value={ticket.subject}
              onSave={async (subject) => {
                await updateMutation.mutateAsync({ subject });
              }}
            />
          </div>

          {/* Description */}
          <div>
            <div className="text-xs uppercase tracking-wider text-muted-foreground mb-2">
              Description
            </div>
            <EditableDescription
              html={body.bodyHtml}
              text={body.bodyText}
              onSave={async (bodyHtml, bodyText) => {
                await updateMutation.mutateAsync({ bodyHtml, bodyText });
              }}
            />
          </div>

          {/* Activity */}
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
