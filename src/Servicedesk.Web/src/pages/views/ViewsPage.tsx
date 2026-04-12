import * as React from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ChevronDown, Eye, Pencil, Plus, Trash2 } from "lucide-react";
import { viewApi, type View, type ViewInput } from "@/lib/ticket-api";
import { taxonomyApi, type Queue, type Priority, type Status } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { cn } from "@/lib/utils";

// ---- Filter shape stored in filtersJson ----

type ViewFilters = {
  queueId?: string;
  statusId?: string;
  priorityId?: string;
  openOnly?: boolean;
  search?: string;
};

function formatFilters(
  filtersJson: string,
  queues: Queue[],
  statuses: Status[],
  priorities: Priority[],
): string[] {
  try {
    const f: ViewFilters = JSON.parse(filtersJson);
    const parts: string[] = [];
    if (f.queueId) {
      const q = queues.find((q) => q.id === f.queueId);
      if (q) parts.push(`Queue: ${q.name}`);
    }
    if (f.statusId) {
      const s = statuses.find((s) => s.id === f.statusId);
      if (s) parts.push(`Status: ${s.name}`);
    }
    if (f.priorityId) {
      const p = priorities.find((p) => p.id === f.priorityId);
      if (p) parts.push(`Priority: ${p.name}`);
    }
    if (f.openOnly) parts.push("Open only");
    if (f.search) parts.push(`Search: "${f.search}"`);
    return parts;
  } catch {
    return [];
  }
}

// ---- Field helper ----

function Field({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="space-y-1.5">
      <label className="text-xs font-medium text-muted-foreground">{label}</label>
      {children}
    </div>
  );
}

// ---- NativeSelect ----

function NativeSelect({
  value,
  onChange,
  children,
}: {
  value: string;
  onChange: (v: string) => void;
  children: React.ReactNode;
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className={cn(
        "w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-foreground",
        "focus:outline-none focus:ring-1 focus:ring-ring focus:border-white/20",
        "disabled:opacity-50",
        "[&_option]:bg-zinc-900 [&_option]:text-foreground",
      )}
    >
      {children}
    </select>
  );
}

// ---- View dialog (create + edit) ----

type ViewDialogProps = {
  view: View | null;
  queues: Queue[];
  statuses: Status[];
  priorities: Priority[];
  onClose: () => void;
  onSaved: () => void;
};

function ViewDialog({
  view,
  queues,
  statuses,
  priorities,
  onClose,
  onSaved,
}: ViewDialogProps) {
  function parseFilters(json: string | undefined): ViewFilters {
    if (!json) return {};
    try {
      return JSON.parse(json);
    } catch {
      return {};
    }
  }

  const initial = parseFilters(view?.filtersJson);

  const [name, setName] = React.useState(view?.name ?? "");
  const [filters, setFilters] = React.useState<ViewFilters>(initial);

  const save = useMutation({
    mutationFn: async () => {
      const input: ViewInput = {
        name,
        filtersJson: JSON.stringify(filters),
      };
      if (view) {
        return viewApi.update(view.id, input);
      }
      return viewApi.create(input);
    },
    onSuccess: () => {
      toast.success(view ? "View updated" : "View created");
      onSaved();
    },
    onError: () => {
      toast.error("Failed to save view");
    },
  });

  function patch(delta: Partial<ViewFilters>) {
    setFilters((f) => ({ ...f, ...delta }));
  }

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{view ? "Edit view" : "New view"}</DialogTitle>
          <DialogDescription>
            Save a filter combination as a named view for quick access.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3 text-sm">
          <Field label="Name">
            <Input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. My open tickets"
              autoFocus
            />
          </Field>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
            <Field label="Queue">
              <NativeSelect
                value={filters.queueId ?? ""}
                onChange={(v) => patch({ queueId: v || undefined })}
              >
                <option value="">Any</option>
                {queues.map((q) => (
                  <option key={q.id} value={q.id}>
                    {q.name}
                  </option>
                ))}
              </NativeSelect>
            </Field>

            <Field label="Status">
              <NativeSelect
                value={filters.statusId ?? ""}
                onChange={(v) => patch({ statusId: v || undefined })}
              >
                <option value="">Any</option>
                {statuses.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name}
                  </option>
                ))}
              </NativeSelect>
            </Field>

            <Field label="Priority">
              <NativeSelect
                value={filters.priorityId ?? ""}
                onChange={(v) => patch({ priorityId: v || undefined })}
              >
                <option value="">Any</option>
                {priorities.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </NativeSelect>
            </Field>
          </div>

          <label className="flex items-center gap-2 text-xs text-muted-foreground cursor-pointer select-none">
            <input
              type="checkbox"
              checked={filters.openOnly ?? false}
              onChange={(e) => patch({ openOnly: e.target.checked || undefined })}
              className="rounded border-white/20"
            />
            Open only (hide resolved &amp; closed tickets)
          </label>
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={save.isPending || !name.trim()}
          >
            {save.isPending ? "Saving..." : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- Delete confirm dialog ----

function DeleteDialog({
  view,
  onClose,
  onDeleted,
}: {
  view: View;
  onClose: () => void;
  onDeleted: () => void;
}) {
  const del = useMutation({
    mutationFn: () => viewApi.remove(view.id),
    onSuccess: () => {
      toast.success("View deleted");
      onDeleted();
    },
    onError: () => {
      toast.error("Failed to delete view");
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle>Delete view</DialogTitle>
          <DialogDescription>
            Delete &ldquo;{view.name}&rdquo;? This cannot be undone.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="destructive"
            disabled={del.isPending}
            onClick={() => del.mutate()}
          >
            {del.isPending ? "Deleting..." : "Delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- View row (expandable bar) ----

function ViewRow({
  view,
  queues,
  statuses,
  priorities,
  expanded,
  onToggle,
  onEdit,
  onDelete,
  onNavigate,
}: {
  view: View;
  queues: Queue[];
  statuses: Status[];
  priorities: Priority[];
  expanded: boolean;
  onToggle: () => void;
  onEdit: () => void;
  onDelete: () => void;
  onNavigate: () => void;
}) {
  const summaryParts = formatFilters(view.filtersJson, queues, statuses, priorities);

  return (
    <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] transition-colors hover:bg-white/[0.04]">
      {/* Main bar */}
      <div className="flex items-center gap-3 px-4 py-2.5">
        <button
          type="button"
          onClick={onToggle}
          className="flex h-6 w-6 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors"
          aria-label={expanded ? "Collapse" : "Expand"}
        >
          <ChevronDown
            className={cn(
              "h-3.5 w-3.5 transition-transform duration-150",
              expanded && "rotate-180",
            )}
          />
        </button>

        <button
          type="button"
          onClick={onNavigate}
          className="flex min-w-0 flex-1 items-center gap-3 text-left"
        >
          <Eye className="h-3.5 w-3.5 shrink-0 text-primary/60" />
          <span className="truncate text-sm font-medium text-foreground">
            {view.name}
          </span>

          {summaryParts.length > 0 && (
            <span className="hidden sm:flex items-center gap-1.5 ml-1">
              {summaryParts.map((part) => (
                <span
                  key={part}
                  className="rounded-full border border-white/8 bg-white/[0.04] px-2 py-0.5 text-[10px] text-muted-foreground whitespace-nowrap"
                >
                  {part}
                </span>
              ))}
            </span>
          )}
        </button>
      </div>

      {/* Expanded actions */}
      {expanded && (
        <div className="flex items-center gap-2 border-t border-white/[0.04] px-4 py-2">
          <Button
            variant="ghost"
            size="sm"
            className="h-7 gap-1.5 text-xs text-muted-foreground"
            onClick={onEdit}
          >
            <Pencil className="h-3 w-3" />
            Edit
          </Button>
          <Button
            variant="ghost"
            size="sm"
            className="h-7 gap-1.5 text-xs text-destructive hover:text-destructive"
            onClick={onDelete}
          >
            <Trash2 className="h-3 w-3" />
            Delete
          </Button>

          {/* Filter summary on mobile (hidden on desktop where it's inline) */}
          {summaryParts.length > 0 && (
            <div className="flex flex-wrap items-center gap-1.5 ml-auto sm:hidden">
              {summaryParts.map((part) => (
                <span
                  key={part}
                  className="rounded-full border border-white/8 bg-white/[0.04] px-2 py-0.5 text-[10px] text-muted-foreground"
                >
                  {part}
                </span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ---- Loading skeleton ----

function ViewRowSkeleton() {
  return (
    <div className="flex items-center gap-3 rounded-lg border border-white/[0.06] bg-white/[0.02] px-4 py-2.5">
      <Skeleton className="h-4 w-4 rounded" />
      <Skeleton className="h-4 w-48" />
      <Skeleton className="ml-auto h-4 w-20 rounded-full" />
    </div>
  );
}

// ---- ViewsPage ----

export function ViewsPage() {
  const qc = useQueryClient();
  const [editingView, setEditingView] = React.useState<View | null | "new">(null);
  const [deletingView, setDeletingView] = React.useState<View | null>(null);
  const [expandedId, setExpandedId] = React.useState<string | null>(null);

  const { data: views, isLoading: viewsLoading } = useQuery({
    queryKey: ["views"],
    queryFn: () => viewApi.list(),
  });

  const { data: queues = [] } = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });

  const { data: statuses = [] } = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
  });

  const { data: priorities = [] } = useQuery({
    queryKey: ["taxonomy", "priorities"],
    queryFn: () => taxonomyApi.priorities.list(),
  });

  function handleSaved() {
    qc.invalidateQueries({ queryKey: ["views"] });
    setEditingView(null);
  }

  function handleDeleted() {
    qc.invalidateQueries({ queryKey: ["views"] });
    setDeletingView(null);
  }

  function navigateToView(id: string) {
    window.location.href = `/tickets?viewId=${id}`;
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/20 border border-primary/30">
            <Eye className="h-4 w-4 text-primary" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold text-foreground leading-tight">
              Views
            </h1>
            {!viewsLoading && (
              <p className="text-xs text-muted-foreground">
                {views?.length ?? 0} saved view{views?.length !== 1 ? "s" : ""}
              </p>
            )}
          </div>
        </div>

        <Button
          onClick={() => setEditingView("new")}
          className="bg-gradient-to-r from-violet-600 to-indigo-600 hover:from-violet-500 hover:to-indigo-500 text-white shadow-[0_0_20px_rgba(124,58,237,0.3)]"
        >
          <Plus className="h-4 w-4" />
          New view
        </Button>
      </header>

      {viewsLoading ? (
        <div className="space-y-2">
          {Array.from({ length: 3 }).map((_, i) => (
            <ViewRowSkeleton key={i} />
          ))}
        </div>
      ) : views && views.length > 0 ? (
        <div className="space-y-1.5">
          {views.map((view) => (
            <ViewRow
              key={view.id}
              view={view}
              queues={queues}
              statuses={statuses}
              priorities={priorities}
              expanded={expandedId === view.id}
              onToggle={() =>
                setExpandedId((prev) => (prev === view.id ? null : view.id))
              }
              onEdit={() => setEditingView(view)}
              onDelete={() => setDeletingView(view)}
              onNavigate={() => navigateToView(view.id)}
            />
          ))}
        </div>
      ) : (
        <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-6 py-10 flex flex-col items-center justify-center gap-4 text-center">
          <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10 border border-primary/20">
            <Eye className="h-5 w-5 text-primary/60" />
          </div>
          <div>
            <p className="text-sm font-medium text-foreground">No views yet</p>
            <p className="text-xs text-muted-foreground mt-1">
              Save a filter combination as a view for quick access.
            </p>
          </div>
          <Button
            onClick={() => setEditingView("new")}
            variant="secondary"
          >
            <Plus className="h-4 w-4" />
            Create your first view
          </Button>
        </div>
      )}

      {editingView && (
        <ViewDialog
          view={editingView === "new" ? null : editingView}
          queues={queues}
          statuses={statuses}
          priorities={priorities}
          onClose={() => setEditingView(null)}
          onSaved={handleSaved}
        />
      )}

      {deletingView && (
        <DeleteDialog
          view={deletingView}
          onClose={() => setDeletingView(null)}
          onDeleted={handleDeleted}
        />
      )}
    </div>
  );
}
