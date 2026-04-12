import * as React from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Check, Copy, Pencil, X } from "lucide-react";
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
    <div className="flex gap-6 pt-3 h-[calc(100vh-0.75rem)] overflow-hidden">
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
  number,
  value,
  onSave,
}: {
  number: number;
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
      <div className="group flex items-center gap-3">
        <TicketNumber number={number} />
        <h1 className="text-2xl font-semibold tracking-tight text-foreground leading-tight flex-1 min-w-0 truncate">
          {value}
        </h1>
        <button
          type="button"
          onClick={() => setEditing(true)}
          className="shrink-0 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-white/[0.06] transition-all"
          title="Edit subject"
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
      </div>
    );
  }

  return (
    <div className="flex items-center gap-3">
      <TicketNumber number={number} />
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

  const isEmpty = !html && !text.trim();

  if (!editing) {
    if (isEmpty) {
      return (
        <button
          type="button"
          onClick={() => setEditing(true)}
          className="w-full flex items-center gap-3 px-4 py-3 rounded-[var(--radius)] border border-white/10 bg-white/[0.03] text-muted-foreground/60 hover:bg-white/[0.06] hover:text-muted-foreground hover:border-white/15 transition-colors text-sm"
        >
          <Pencil className="h-4 w-4 shrink-0" />
          Add a description...
        </button>
      );
    }

    return (
      <div
        className="group relative rounded-[var(--radius)] border border-white/10 bg-white/[0.03] px-4 py-3 cursor-pointer hover:bg-white/[0.06] hover:border-white/15 transition-colors max-h-32 overflow-y-auto"
        onClick={() => setEditing(true)}
        title="Click to edit description"
      >
        <button
          type="button"
          className="absolute top-2 right-2 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-white/[0.06] transition-all z-10"
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
        <RichTextEditor
          content={html ?? text}
          editable={false}
          minHeight="0px"
          className="border-none bg-transparent !rounded-none"
        />
      </div>
    );
  }

  return (
    <div className="rounded-[var(--radius)] border border-white/10 bg-white/[0.04] p-4 space-y-3">
      <RichTextEditor
        content={draftHtml}
        onChange={setDraftHtml}
        placeholder="Describe the issue..."
        minHeight="100px"
      />
      <div className="flex items-center justify-between">
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
    <div className="flex gap-6 pt-3 h-[calc(100vh-0.75rem)] overflow-hidden">
      {/* Left column — header + description static, activity scrolls, reply pinned bottom */}
      <div className="flex flex-col flex-1 min-w-0 min-h-0 overflow-hidden">
        {/* Static: ticket number + subject on one line */}
        <div className="shrink-0 pb-4">
          <EditableSubject
            number={ticket.number}
            value={ticket.subject}
            onSave={async (subject) => {
              await updateMutation.mutateAsync({ subject });
            }}
          />
        </div>

        {/* Static: description */}
        <div className="shrink-0 pb-4">
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

        {/* Static: activity divider */}
        <div className="shrink-0 pb-3">
          <div className="flex items-center gap-3">
            <div className="h-px flex-1 bg-white/10" />
            <span className="text-xs uppercase tracking-wider text-muted-foreground">
              Activity
            </span>
            <div className="h-px flex-1 bg-white/10" />
          </div>
        </div>

        {/* Scrollable: activity timeline */}
        <div className="flex-1 min-h-0 overflow-y-auto pr-1">
          <TicketTimeline events={events} />
        </div>

        {/* Static: reply form */}
        <div className="shrink-0 pt-3">
          <AddNoteForm
            ticketId={ticketId}
            onSubmitted={() => {
              queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
            }}
          />
        </div>
      </div>

      {/* Right column — side panel, full height */}
      <TicketSidePanel
        ticket={ticket}
        onUpdate={async (fields) => { await updateMutation.mutateAsync(fields); }}
      />
    </div>
  );
}
