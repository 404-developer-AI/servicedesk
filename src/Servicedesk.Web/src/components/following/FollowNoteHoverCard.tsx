import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { NotebookPen, Pencil } from "lucide-react";
import { toast } from "sonner";
import { HoverCard, HoverCardContent, HoverCardTrigger } from "@/components/ui/hover-card";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { errorMessage } from "@/lib/http";
import { FOLLOW_NOTE_MAX, FOLLOWING_KEY, followingApi, withNote, type FollowingResponse } from "@/lib/following-api";

/// v0.1.58 — your personal note on a followed ticket ("why am I watching
/// this?"). Hovering the trigger shows the note, or an "Add a note" button
/// when there is none; editing keeps the card open until you save, cancel
/// (Esc) or click elsewhere. Bump `editRequest` to open it straight into
/// editing — the Follow button does that right after a follow.
export function FollowNoteHoverCard({
  ticketId,
  note,
  enabled = true,
  editRequest = 0,
  side = "bottom",
  align = "start",
  children,
}: {
  ticketId: string;
  note: string | null;
  /// Off: the trigger renders without a card (e.g. a ticket you don't follow).
  enabled?: boolean;
  editRequest?: number;
  side?: "top" | "right" | "bottom" | "left";
  align?: "start" | "center" | "end";
  children: React.ReactNode;
}) {
  const [hoverOpen, setHoverOpen] = React.useState(false);
  const [editing, setEditing] = React.useState(false);

  React.useEffect(() => {
    if (editRequest > 0) setEditing(true);
  }, [editRequest]);

  if (!enabled) return <>{children}</>;

  return (
    <HoverCard
      open={hoverOpen || editing}
      onOpenChange={setHoverOpen}
      openDelay={300}
      closeDelay={150}
    >
      <HoverCardTrigger asChild>{children}</HoverCardTrigger>
      <HoverCardContent
        side={side}
        align={align}
        className="sd-follow-note w-72 p-3"
        onPointerDownOutside={() => {
          setEditing(false);
          setHoverOpen(false);
        }}
      >
        {editing ? (
          <NoteEditor
            ticketId={ticketId}
            initial={note ?? ""}
            onDone={() => {
              setEditing(false);
              setHoverOpen(false);
            }}
          />
        ) : (
          <NoteView note={note} onEdit={() => setEditing(true)} />
        )}
      </HoverCardContent>
    </HoverCard>
  );
}

function NoteView({ note, onEdit }: { note: string | null; onEdit: () => void }) {
  if (!note) {
    return (
      <button type="button" onClick={onEdit}
        className="flex w-full items-center gap-2 rounded-md border border-dashed border-glass px-2 py-2 text-left text-xs text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground">
        <NotebookPen className="h-3.5 w-3.5 shrink-0" />
        Add a note on why you follow this ticket
      </button>
    );
  }
  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between gap-2">
        <span className="text-[11px] font-medium text-muted-foreground">Your note</span>
        <button type="button" onClick={onEdit} title="Edit note" aria-label="Edit note"
          className="rounded-md p-1 text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground">
          <Pencil className="h-3.5 w-3.5" />
        </button>
      </div>
      <p className="max-h-48 overflow-y-auto whitespace-pre-wrap break-words text-xs leading-relaxed text-foreground">
        {note}
      </p>
    </div>
  );
}

function NoteEditor({ ticketId, initial, onDone }: { ticketId: string; initial: string; onDone: () => void }) {
  const qc = useQueryClient();
  const [value, setValue] = React.useState(initial);
  const ref = React.useRef<HTMLTextAreaElement>(null);

  React.useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.focus();
    el.setSelectionRange(el.value.length, el.value.length);
  }, []);

  const save = useMutation({
    mutationFn: (note: string | null) => followingApi.setNote(ticketId, note),
    onMutate: (note) => qc.setQueryData<FollowingResponse>(FOLLOWING_KEY, (old) => withNote(old, ticketId, note)),
    onSuccess: () => onDone(),
    onError: (e) => toast.error(errorMessage(e, "Could not save the note")),
    onSettled: () => void qc.invalidateQueries({ queryKey: FOLLOWING_KEY }),
  });

  const submit = () => {
    const note = value.trim();
    if (note === initial.trim()) return onDone();
    save.mutate(note.length === 0 ? null : note);
  };

  const tooLong = value.length > FOLLOW_NOTE_MAX;

  return (
    <div className="space-y-2">
      <label htmlFor={`follow-note-${ticketId}`} className="text-[11px] font-medium text-muted-foreground">
        Why are you following this ticket?
      </label>
      <textarea
        id={`follow-note-${ticketId}`}
        ref={ref}
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            e.stopPropagation();
            onDone();
          } else if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
            e.preventDefault();
            if (!tooLong) submit();
          }
        }}
        rows={3}
        placeholder="Optional — only you can see this"
        className="w-full resize-none rounded-md border border-glass bg-glass px-2 py-1.5 text-xs outline-hidden focus:border-glass-strong"
      />
      <div className="flex items-center justify-between gap-2">
        <span className={cn("text-[10px]", tooLong ? "text-destructive" : "text-muted-foreground/70")}>
          {value.length}/{FOLLOW_NOTE_MAX} · Enter saves, Shift+Enter new line
        </span>
        <div className="flex gap-1">
          <Button type="button" variant="ghost" size="sm" className="h-7 px-2" onClick={onDone}>Cancel</Button>
          <Button type="button" size="sm" className="h-7 px-2" disabled={tooLong || save.isPending} onClick={submit}>Save</Button>
        </div>
      </div>
    </div>
  );
}
