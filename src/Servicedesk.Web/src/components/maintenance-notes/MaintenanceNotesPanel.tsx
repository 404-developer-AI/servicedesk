import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Pencil, Plus, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { NoteBodyHtml } from "@/components/NoteBodyHtml";
import { RichTextEditor } from "@/components/RichTextEditor";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateMedium } from "@/lib/dateFormat";
import { errorMessage } from "@/lib/http";
import { MAINTENANCE_NOTES_KEY, type MaintenanceNote, type MaintenanceNotesDoor } from "@/lib/maintenance-notes-api";
import { cn } from "@/lib/utils";
import { MaintenanceNotesHistoryDialog } from "./MaintenanceNotesHistoryDialog";

/// v0.1.67 — the maintenance notes of one plan member, the same on a round
/// ticket and in the planner: the original (oldest note written in the
/// planner) and the additions, each with who / when / during which ticket,
/// inline edit, soft delete, and the full history. `door` decides where the
/// writes go (ticket → recorded against that ticket; planner → none).
export function MaintenanceNotesPanel({
  door, currentTicketId, emptyHint,
}: {
  door: MaintenanceNotesDoor;
  /** On a ticket: additions written here read "this intervention". */
  currentTicketId?: string;
  emptyHint: string;
}) {
  const qc = useQueryClient();
  const zone = useServerTimeZone();
  const view = useQuery({ queryKey: door.key, queryFn: door.list, staleTime: 30_000 });
  const [editing, setEditing] = React.useState<string | null>(null);
  const [draft, setDraft] = React.useState("");
  const [historyOpen, setHistoryOpen] = React.useState(false);

  const done = (message: string) => {
    void qc.invalidateQueries({ queryKey: MAINTENANCE_NOTES_KEY });
    setEditing(null);
    setDraft("");
    toast.success(message);
  };
  const save = useMutation({
    mutationFn: () => (editing === "new" ? door.add(draft) : door.update(editing!, draft)),
    onSuccess: () => done(editing === "new" ? "Note added" : "Note saved"),
    onError: (err) => toast.error(errorMessage(err, "Could not save the note")),
  });
  const remove = useMutation({
    mutationFn: (noteId: string) => door.remove(noteId),
    onSuccess: () => done("Note removed (still in the history)"),
    onError: (err) => toast.error(errorMessage(err, "Could not remove the note")),
  });

  const notes = view.data?.notes ?? [];
  const originalId = notes.find((n) => !n.ticketId)?.id ?? null;
  const startEdit = (n: MaintenanceNote | null) => { setEditing(n?.id ?? "new"); setDraft(n?.bodyHtml ?? ""); };

  const label = (n: MaintenanceNote) => {
    const who = n.createdByEmail ?? "Unknown user";
    const when = formatDateMedium(n.createdUtc, zone);
    const where = n.id === originalId ? "Original · written in the planner"
      : !n.ticketId ? "Added in the planner"
      : n.ticketId === currentTicketId ? "Added during this intervention"
      : n.ticketNumber ? `Added during #${n.ticketNumber}` : "Added during an intervention";
    const edited = n.updatedUtc ? ` · edited ${formatDateMedium(n.updatedUtc, zone)}${n.updatedByEmail ? ` by ${n.updatedByEmail}` : ""}` : "";
    return `${where} · ${who} · ${when}${edited}`;
  };

  const editor = (
    <div className="space-y-2">
      <RichTextEditor content={draft} onChange={setDraft} minHeight="90px" maxHeight="320px" autoFocus
        placeholder="What should the next technician know? No passwords — use the password vault." />
      <div className="flex justify-end gap-2">
        <Button type="button" size="sm" variant="ghost" onClick={() => setEditing(null)} disabled={save.isPending}>Cancel</Button>
        <Button type="button" size="sm" onClick={() => save.mutate()} disabled={save.isPending || !draft.replace(/<[^>]*>/g, "").trim()}>
          {save.isPending && <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />}Save
        </Button>
      </div>
    </div>
  );

  return (
    <div className="space-y-2.5 text-sm">
      {view.isLoading ? (
        <p className="text-xs text-muted-foreground">Loading…</p>
      ) : view.isError ? (
        <p className="text-xs text-destructive">Could not load the notes.</p>
      ) : notes.length === 0 && editing !== "new" ? (
        <p className="text-xs text-muted-foreground">{emptyHint}</p>
      ) : (
        notes.map((n) => (
          <div key={n.id} className={cn("group", n.id !== originalId && "sd-maintenance-note-addition border-l-2 border-primary pl-2.5")}>
            <div className="mb-0.5 flex flex-wrap items-center gap-x-2 text-[11px] text-muted-foreground">
              <span>{label(n)}</span>
              {editing !== n.id && (
                <span className="flex gap-1 opacity-70 transition-opacity group-hover:opacity-100">
                  <button type="button" className="inline-flex items-center gap-0.5 hover:text-foreground" onClick={() => startEdit(n)}>
                    <Pencil className="h-3 w-3" /> Edit
                  </button>
                  <button type="button" className="inline-flex items-center gap-0.5 hover:text-destructive" disabled={remove.isPending}
                    onClick={() => { if (window.confirm("Remove this note? It stays in the history.")) remove.mutate(n.id); }}>
                    <Trash2 className="h-3 w-3" /> Remove
                  </button>
                </span>
              )}
            </div>
            {editing === n.id ? editor : <NoteBodyHtml html={n.bodyHtml} />}
          </div>
        ))
      )}
      {editing === "new" && <div className="border-l-2 border-primary pl-2.5">{editor}</div>}
      <div className="flex items-center gap-3">
        {editing === null && (
          <Button type="button" size="sm" variant="outline" onClick={() => startEdit(null)}>
            <Plus className="mr-1 h-3.5 w-3.5" /> {notes.length === 0 ? "Write the first note" : "Add to notes"}
          </Button>
        )}
        <button type="button" className="text-xs text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
          onClick={() => setHistoryOpen(true)}>History</button>
        <span className="ml-auto text-[11px] text-muted-foreground">Internal · no passwords or credentials</span>
      </div>
      <MaintenanceNotesHistoryDialog door={door} open={historyOpen} onOpenChange={setHistoryOpen} />
    </div>
  );
}
