import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import {
  Zap, Plus, Pencil, Trash2, Activity,
  Clock, AlertTriangle, CheckCircle2, MinusCircle, History,
} from "lucide-react";
import { triggerApi, type TriggerListItem } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { TriggerEditorSheet } from "./triggers/TriggerEditorSheet";
import { cn } from "@/lib/utils";

export function TriggersSettingsPage({ initialEditId }: { initialEditId?: string } = {}) {
  const qc = useQueryClient();
  const [editing, setEditing] = React.useState<string | "new" | null>(initialEditId ?? null);
  const [deleting, setDeleting] = React.useState<TriggerListItem | null>(null);

  // Sync with route changes — when an admin lands on /settings/triggers/{id}
  // from a search-hit and then navigates to a different trigger via search,
  // the prop changes and the editor must follow.
  React.useEffect(() => {
    if (initialEditId) setEditing(initialEditId);
  }, [initialEditId]);

  const listQ = useQuery({
    queryKey: ["admin", "triggers"],
    queryFn: () => triggerApi.list(),
  });
  const metaQ = useQuery({
    queryKey: ["admin", "trigger-metadata"],
    queryFn: () => triggerApi.metadata(),
  });

  const toggle = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) =>
      triggerApi.setActive(id, isActive),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["admin", "triggers"] });
    },
    onError: () => toast.error("Failed to toggle trigger."),
  });

  const items = listQ.data?.items ?? [];
  const window = listQ.data?.runSummaryWindowHours ?? 24;

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/20 border border-primary/30">
            <Zap className="h-4 w-4 text-primary" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold text-foreground leading-tight">
              Triggers
            </h1>
            <p className="text-xs text-muted-foreground">
              Auto-route, auto-reply and auto-escalate. Each trigger fires on
              an activator and applies its actions when the conditions match.
            </p>
          </div>
        </div>
        <Button
          onClick={() => setEditing("new")}
          className="bg-gradient-to-r from-violet-600 to-indigo-600 hover:from-violet-500 hover:to-indigo-500 text-white shadow-[0_0_20px_rgba(124,58,237,0.3)]"
        >
          <Plus className="h-4 w-4" />
          New trigger
        </Button>
      </header>

      {listQ.isLoading ? (
        <div className="space-y-2">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-14 rounded-lg" />
          ))}
        </div>
      ) : items.length === 0 ? (
        <EmptyState onNew={() => setEditing("new")} />
      ) : (
        <div className="space-y-2">
          {items.map((t) => (
            <TriggerRow
              key={t.id}
              item={t}
              window={window}
              onEdit={() => setEditing(t.id)}
              onDelete={() => setDeleting(t)}
              onToggle={(v) => toggle.mutate({ id: t.id, isActive: v })}
              toggleLoading={toggle.isPending}
            />
          ))}
        </div>
      )}

      {metaQ.data && (
        <TriggerEditorSheet
          triggerId={editing}
          metadata={metaQ.data}
          onClose={() => setEditing(null)}
        />
      )}

      {deleting && (
        <DeleteDialog
          item={deleting}
          onClose={() => setDeleting(null)}
          onDeleted={() => {
            qc.invalidateQueries({ queryKey: ["admin", "triggers"] });
            setDeleting(null);
          }}
        />
      )}
    </div>
  );
}

function TriggerRow({
  item,
  window,
  onEdit,
  onDelete,
  onToggle,
  toggleLoading,
}: {
  item: TriggerListItem;
  window: number;
  onEdit: () => void;
  onDelete: () => void;
  onToggle: (v: boolean) => void;
  toggleLoading: boolean;
}) {
  const totalRuns =
    item.runs.applied + item.runs.skippedNoMatch + item.runs.skippedLoop + item.runs.failed;
  return (
    <div
      className={cn(
        "rounded-lg border bg-white/[0.02] px-4 py-3 transition-colors",
        item.isActive
          ? "border-white/[0.06] hover:bg-white/[0.04]"
          : "border-white/[0.04] opacity-70",
      )}
    >
      <div className="flex items-center gap-3">
        <Switch
          checked={item.isActive}
          disabled={toggleLoading}
          onCheckedChange={onToggle}
        />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={onEdit}
              className="truncate text-left text-sm font-medium text-foreground hover:text-primary"
            >
              {item.name}
            </button>
            <ActivatorBadge kind={item.activatorKind} mode={item.activatorMode} />
            {item.locale && (
              <span className="rounded-full border border-white/8 bg-white/[0.04] px-2 py-0.5 text-[10px] text-muted-foreground">
                {item.locale}
              </span>
            )}
          </div>
          {item.description && (
            <p className="mt-0.5 truncate text-xs text-muted-foreground/70">
              {item.description}
            </p>
          )}
          <div className="mt-1.5 flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground/80">
            <span title={`Runs in the last ${window}h`} className="flex items-center gap-1">
              <Activity className="h-3 w-3" />
              {totalRuns} run{totalRuns === 1 ? "" : "s"} / {window}h
            </span>
            {item.runs.applied > 0 && (
              <span className="flex items-center gap-1 text-emerald-300">
                <CheckCircle2 className="h-3 w-3" />
                {item.runs.applied} applied
              </span>
            )}
            {item.runs.skippedNoMatch > 0 && (
              <span className="flex items-center gap-1 text-muted-foreground">
                <MinusCircle className="h-3 w-3" />
                {item.runs.skippedNoMatch} skipped
              </span>
            )}
            {item.runs.failed > 0 && (
              <span className="flex items-center gap-1 text-red-300">
                <AlertTriangle className="h-3 w-3" />
                {item.runs.failed} failed
              </span>
            )}
            {item.runs.lastFiredUtc && (
              <span className="flex items-center gap-1">
                <Clock className="h-3 w-3" />
                Last: {formatRelative(item.runs.lastFiredUtc)}
              </span>
            )}
          </div>
        </div>
        <div className="flex items-center gap-1">
          <Button variant="ghost" size="sm" asChild className="h-8 gap-1.5">
            <Link to="/settings/triggers/$triggerId/runs" params={{ triggerId: item.id }}>
              <History className="h-3.5 w-3.5" />
              Runs
            </Link>
          </Button>
          <Button variant="ghost" size="sm" onClick={onEdit} className="h-8 gap-1.5">
            <Pencil className="h-3.5 w-3.5" />
            Edit
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={onDelete}
            className="h-8 gap-1.5 text-destructive hover:text-destructive"
          >
            <Trash2 className="h-3.5 w-3.5" />
            Delete
          </Button>
        </div>
      </div>
    </div>
  );
}

function ActivatorBadge({ kind, mode }: { kind: string; mode: string }) {
  const label =
    kind === "action"
      ? mode === "selective" ? "On update (selective)" : "On update (always)"
      : mode === "reminder" ? "Pending-till"
      : mode === "escalation" ? "SLA breach"
      : mode === "escalation_warning" ? "SLA warning"
      : `${kind}:${mode}`;
  const tone = kind === "action" ? "violet" : "amber";
  return (
    <span
      className={cn(
        "rounded-full border px-2 py-0.5 text-[10px] font-medium whitespace-nowrap",
        tone === "violet"
          ? "border-violet-400/30 bg-violet-400/10 text-violet-200"
          : "border-amber-400/30 bg-amber-400/10 text-amber-200",
      )}
    >
      {label}
    </span>
  );
}

function EmptyState({ onNew }: { onNew: () => void }) {
  return (
    <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-6 py-12 flex flex-col items-center justify-center gap-4 text-center">
      <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10 border border-primary/20">
        <Zap className="h-5 w-5 text-primary/60" />
      </div>
      <div>
        <p className="text-sm font-medium text-foreground">No triggers yet</p>
        <p className="text-xs text-muted-foreground mt-1">
          Create one to auto-route, auto-reply, or escalate on SLA breach.
        </p>
      </div>
      <Button onClick={onNew} variant="secondary">
        <Plus className="h-4 w-4" />
        Create your first trigger
      </Button>
    </div>
  );
}

function DeleteDialog({
  item,
  onClose,
  onDeleted,
}: {
  item: TriggerListItem;
  onClose: () => void;
  onDeleted: () => void;
}) {
  const del = useMutation({
    mutationFn: () => triggerApi.remove(item.id),
    onSuccess: () => {
      toast.success("Trigger deleted");
      onDeleted();
    },
    onError: () => toast.error("Failed to delete trigger."),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle>Delete trigger</DialogTitle>
          <DialogDescription>
            Delete &ldquo;{item.name}&rdquo;? Run history is removed too. This
            cannot be undone.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button
            variant="destructive"
            disabled={del.isPending}
            onClick={() => del.mutate()}
          >
            {del.isPending ? "Deleting…" : "Delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function formatRelative(iso: string): string {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return iso;
  const diffMs = Date.now() - t;
  const diffMin = Math.round(diffMs / 60_000);
  if (diffMin < 1) return "just now";
  if (diffMin < 60) return `${diffMin} min ago`;
  const diffH = Math.round(diffMin / 60);
  if (diffH < 24) return `${diffH} h ago`;
  const diffD = Math.round(diffH / 24);
  return `${diffD} d ago`;
}
