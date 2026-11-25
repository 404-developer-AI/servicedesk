import { useQuery } from "@tanstack/react-query";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { NoteBodyHtml } from "@/components/NoteBodyHtml";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTimeMedium } from "@/lib/dateFormat";
import type { MaintenanceNotesDoor } from "@/lib/maintenance-notes-api";

const ACTION_TEXT = { Added: "Added", Edited: "Edited", Deleted: "Removed" } as const;

/// Every version of a member's maintenance notes, newest first: who, when,
/// during which ticket, and the text as it was saved (removed notes too).
export function MaintenanceNotesHistoryDialog({
  door, open, onOpenChange,
}: {
  door: MaintenanceNotesDoor;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const zone = useServerTimeZone();
  const history = useQuery({ queryKey: [...door.key, "history"], queryFn: door.history, enabled: open });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="flex max-h-[85vh] max-w-2xl flex-col">
        <DialogHeader>
          <DialogTitle>Maintenance notes — history</DialogTitle>
          <DialogDescription>Every saved version, newest first. Removed notes stay here.</DialogDescription>
        </DialogHeader>
        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1">
          {history.isLoading && <p className="text-sm text-muted-foreground">Loading…</p>}
          {history.isError && <p className="text-sm text-destructive">Could not load the history.</p>}
          {history.data?.items.length === 0 && <p className="text-sm text-muted-foreground">No changes yet.</p>}
          {history.data?.items.map((r, i) => (
            <div key={`${r.noteId}-${r.createdUtc}-${i}`} className="rounded-md border border-glass p-2.5">
              <div className="mb-1 text-[11px] text-muted-foreground">
                <span className="font-medium text-foreground">{ACTION_TEXT[r.action]}</span>
                {" · "}{r.byEmail ?? "Unknown user"}{" · "}{formatDateTimeMedium(r.createdUtc, zone)}
                {r.ticketNumber ? ` · during #${r.ticketNumber}` : ""}
              </div>
              <NoteBodyHtml html={r.bodyHtml} className={`rich-prose rich-prose-sm${r.action === "Deleted" ? " line-through opacity-60" : ""}`} />
            </div>
          ))}
        </div>
      </DialogContent>
    </Dialog>
  );
}
