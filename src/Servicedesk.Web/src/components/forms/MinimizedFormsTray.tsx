import * as React from "react";
import { useNavigate, useRouterState } from "@tanstack/react-router";
import { ChevronUp, ClipboardList, PanelTop, X } from "lucide-react";
import { useConfirm } from "@/components/ConfirmDialog";
import { cn } from "@/lib/utils";
import { useWorkspaceStore, type FormDraftEntry } from "@/stores/useWorkspaceStore";
import { useTicketOverlayStore, type TicketOverlayEntry } from "@/stores/useTicketOverlayStore";

/// v0.1.55 — minimised ticket forms, visible on every page of the shell so
/// an agent can copy text from another ticket and come back. A chip always
/// says which ticket the form belongs to; restoring goes back to that
/// ticket first, so a form can never be saved on the wrong one.
/// v0.1.61 — minimised ticket overlays share the tray (panel icon instead of
/// the form icon). Restoring one returns to the list or view tab it was
/// opened from, then re-opens the overlay.

const MAX_EXPANDED = 3;

export function MinimizedFormsTray() {
  const formDrafts = useWorkspaceStore((s) => s.formDrafts);
  const requestFormRestore = useWorkspaceStore((s) => s.requestFormRestore);
  const removeFormDraft = useWorkspaceStore((s) => s.removeFormDraft);
  const navigate = useNavigate();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const confirm = useConfirm();
  const [expanded, setExpanded] = React.useState(false);
  const overlays = useTicketOverlayStore((s) => s.minimized);
  const restoreOverlay = useTicketOverlayStore((s) => s.restore);
  const discardOverlay = useTicketOverlayStore((s) => s.discard);
  const search = useRouterState({ select: (s) => s.location.searchStr });

  const entries = React.useMemo(
    () => Object.values(formDrafts)
      .filter((d) => d.minimized)
      .sort((a, b) => b.updatedUtc.localeCompare(a.updatedUtc)),
    [formDrafts],
  );
  if (entries.length === 0 && overlays.length === 0) return null;

  const reopenOverlay = (o: TicketOverlayEntry) => {
    const src = o.source;
    if (src && (pathname !== src.path || search !== src.search)) {
      void navigate({ to: src.path + src.search });
    }
    restoreOverlay(o.ticketId);
  };

  const restore = (entry: FormDraftEntry) => {
    requestFormRestore(entry.key);
    // A form filled in the ticket overlay comes back in that overlay (its
    // form host picks the restore up), never on the full ticket page.
    const overlay = useTicketOverlayStore.getState();
    if (overlay.current?.ticketId === entry.ticketId) return;
    const minimizedOverlay = overlays.find((o) => o.ticketId === entry.ticketId);
    if (minimizedOverlay) {
      reopenOverlay(minimizedOverlay);
      return;
    }
    if (pathname !== `/tickets/${entry.ticketId}`) {
      void navigate({ to: "/tickets/$ticketId", params: { ticketId: entry.ticketId } });
    }
  };

  const discard = async (entry: FormDraftEntry) => {
    const ok = await confirm({
      title: "Discard form",
      description: `Discard the answers in "${entry.formName}"${entry.ticketNumber ? ` on #${entry.ticketNumber}` : ""}?`,
      confirmLabel: "Discard",
      tone: "destructive",
    });
    if (ok) removeFormDraft(entry.key);
  };

  const collapsed = entries.length > MAX_EXPANDED && !expanded;
  const visible = collapsed ? entries.slice(0, MAX_EXPANDED - 1) : entries;

  return (
    // z above the ticket overlay (z-50) so a form minimised inside it stays
    // visible; the overlay ignores clicks on the tray (no auto-minimise).
    <div className="sd-form-tray pointer-events-none fixed bottom-3 left-1/2 z-[51] flex -translate-x-1/2 flex-wrap-reverse items-end justify-center gap-2"
      role="region" aria-label="Minimised forms and tickets">
      {overlays.map((o) => (
        <div key={`overlay-${o.ticketId}`}
          className="glass-card pointer-events-auto flex h-9 max-w-[20rem] items-center gap-1 rounded-lg border border-glass-strong pl-3 pr-1 text-sm shadow-lg">
          <button type="button" onClick={() => reopenOverlay(o)}
            title="Open this ticket again"
            className="flex min-w-0 items-center gap-2 text-left text-foreground/90 hover:text-foreground">
            <PanelTop className="h-4 w-4 shrink-0 text-primary" />
            <span className="min-w-0 truncate">
              {o.ticketNumber ? <span className="font-medium tabular-nums">#{o.ticketNumber}</span> : null}
              {o.ticketNumber && o.subject ? " · " : ""}
              {o.subject ?? ""}
            </span>
            <ChevronUp className="h-4 w-4 shrink-0 text-muted-foreground" />
          </button>
          <button type="button" onClick={() => discardOverlay(o.ticketId)} aria-label={`Close ticket ${o.ticketNumber ?? ""}`}
            className="flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-glass-hover hover:text-foreground">
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      ))}
      {visible.map((entry) => (
        <div key={entry.key}
          className="glass-card pointer-events-auto flex h-9 max-w-[20rem] items-center gap-1 rounded-lg border border-glass-strong pl-3 pr-1 text-sm shadow-lg">
          <button type="button" onClick={() => restore(entry)}
            title={`Open "${entry.formName}" again`}
            className="flex min-w-0 items-center gap-2 text-left text-foreground/90 hover:text-foreground">
            <ClipboardList className="h-4 w-4 shrink-0 text-primary" />
            <span className="min-w-0 truncate">
              {entry.ticketNumber ? <span className="font-medium tabular-nums">#{entry.ticketNumber}</span> : null}
              {entry.ticketNumber ? " · " : ""}
              {entry.formName}
              {entry.mode === "edit" && <span className="text-muted-foreground"> (edit)</span>}
            </span>
            <ChevronUp className="h-4 w-4 shrink-0 text-muted-foreground" />
          </button>
          <button type="button" onClick={() => void discard(entry)} aria-label={`Discard ${entry.formName}`}
            className="flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-glass-hover hover:text-foreground">
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      ))}
      {entries.length > MAX_EXPANDED && (
        <button type="button" onClick={() => setExpanded((v) => !v)}
          className={cn(
            "glass-card pointer-events-auto flex h-9 items-center gap-2 rounded-lg border border-glass-strong px-3 text-sm shadow-lg",
            "text-muted-foreground hover:text-foreground",
          )}>
          <ClipboardList className="h-4 w-4 text-primary" />
          {collapsed ? `+${entries.length - visible.length} forms` : "Show less"}
        </button>
      )}
    </div>
  );
}
