import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { FileText } from "lucide-react";
import { MaintenanceNotesPanel } from "@/components/maintenance-notes/MaintenanceNotesPanel";
import { memberNotesDoor } from "@/lib/maintenance-notes-api";
import { cn } from "@/lib/utils";

/// v0.1.67 — the notes icon in a customer row of the plan: highlighted with
/// a count when the customer has maintenance notes. The count follows the
/// notes panel once it was opened (cache only — no request per row).
export function MemberNotesButton({
  memberId, initialCount, open, onToggle,
}: {
  memberId: string | null;
  initialCount: number;
  open: boolean;
  onToggle: () => void;
}) {
  const door = React.useMemo(() => (memberId ? memberNotesDoor(memberId) : null), [memberId]);
  const cached = useQuery({ queryKey: door?.key ?? ["maintenance-notes", "none"], queryFn: door?.list ?? (async () => null), enabled: false });
  const count = cached.data?.notes.length ?? initialCount;

  if (!memberId) {
    return (
      <span className="grid h-8 w-8 place-items-center text-muted-foreground/40" title="Save the plan first, then add notes for this customer">
        <FileText className="h-3.5 w-3.5" />
      </span>
    );
  }
  return (
    <button type="button" onClick={onToggle} aria-expanded={open}
      title={count > 0 ? `Maintenance notes (${count})` : "Add maintenance notes"}
      className={cn("relative grid h-8 w-8 place-items-center rounded-md transition-colors",
        count > 0 || open ? "bg-primary/10 text-primary" : "text-muted-foreground hover:text-foreground")}>
      <FileText className="h-3.5 w-3.5" />
      {count > 0 && (
        <span className="absolute -right-0.5 -top-0.5 min-w-3.5 rounded-full bg-primary px-1 text-[9px] font-bold leading-3.5 text-primary-foreground">
          {count}
        </span>
      )}
    </button>
  );
}

/// The member's notes, unfolded under its row. Saves immediately (independent of the plan's Save).
export function MemberNotesRow({ memberId, title }: { memberId: string; title: string }) {
  const door = React.useMemo(() => memberNotesDoor(memberId), [memberId]);
  return (
    <div className="sd-maintenance-member-notes space-y-2 border-t border-dashed border-primary/30 bg-primary/5 px-3.5 py-3">
      <div className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Maintenance notes · {title}</div>
      <MaintenanceNotesPanel door={door}
        emptyHint="No notes yet. They are shown on every maintenance ticket of this customer, and technicians can add to them there. Notes are saved right away." />
    </div>
  );
}
