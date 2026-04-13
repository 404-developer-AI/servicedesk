import * as React from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { toast } from "sonner";
import { ArrowDown, ArrowUp, ChevronDown, Eye, Pencil, Plus, Trash2 } from "lucide-react";
import { viewApi, type View, type ViewInput, type DisplayConfig } from "@/lib/ticket-api";
import { taxonomyApi, type Queue, type Priority, type Status, type Category } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { cn } from "@/lib/utils";

// ---- Column definitions ----

const ALL_COLUMNS: { id: string; label: string }[] = [
  { id: "number", label: "Number" },
  { id: "subject", label: "Subject" },
  { id: "requester", label: "Requester" },
  { id: "companyName", label: "Company" },
  { id: "queueName", label: "Queue" },
  { id: "statusName", label: "Status" },
  { id: "priorityName", label: "Priority" },
  { id: "categoryName", label: "Category" },
  { id: "assigneeEmail", label: "Assignee" },
  { id: "createdUtc", label: "Created" },
  { id: "updatedUtc", label: "Updated" },
  { id: "dueUtc", label: "Due" },
];

// ---- Sort field options ----

const SORT_FIELDS: { value: string; label: string }[] = [
  { value: "updatedUtc", label: "Updated" },
  { value: "createdUtc", label: "Created" },
  { value: "dueUtc", label: "Due date" },
  { value: "priorityLevel", label: "Priority" },
  { value: "number", label: "Ticket #" },
  { value: "subject", label: "Subject" },
  { value: "statusName", label: "Status" },
  { value: "queueName", label: "Queue" },
  { value: "assigneeEmail", label: "Assignee" },
  { value: "requesterEmail", label: "Requester" },
  { value: "companyName", label: "Company" },
  { value: "categoryName", label: "Category" },
];

// ---- Group-by options ----

const GROUP_BY_OPTIONS: { value: string; label: string; hasTaxonomy: boolean }[] = [
  { value: "", label: "None", hasTaxonomy: false },
  { value: "statusId", label: "Status", hasTaxonomy: true },
  { value: "priorityId", label: "Priority", hasTaxonomy: true },
  { value: "queueId", label: "Queue", hasTaxonomy: true },
  { value: "assigneeUserId", label: "Assignee", hasTaxonomy: false },
  { value: "categoryId", label: "Category", hasTaxonomy: true },
  { value: "companyName", label: "Company", hasTaxonomy: false },
  { value: "requesterContactId", label: "Requester", hasTaxonomy: false },
];

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

function formatDisplayConfig(dc: DisplayConfig): string[] {
  const parts: string[] = [];
  if (dc.priorityFloat) parts.push("Priority float");
  if (dc.groupBy) {
    const opt = GROUP_BY_OPTIONS.find((o) => o.value === dc.groupBy);
    if (opt) parts.push(`Group: ${opt.label}`);
  }
  if (dc.sort?.field) {
    const sf = SORT_FIELDS.find((f) => f.value === dc.sort!.field);
    const dir = dc.sort.direction === "asc" ? "\u2191" : "\u2193";
    if (sf) parts.push(`Sort: ${sf.label} ${dir}`);
  }
  return parts;
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

// ---- Group order editor ----

function GroupOrderEditor({
  groupBy,
  groupOrder,
  onChange,
  statuses,
  priorities,
  queues,
  categories,
}: {
  groupBy: string;
  groupOrder: string[];
  onChange: (order: string[]) => void;
  statuses: Status[];
  priorities: Priority[];
  queues: Queue[];
  categories: Category[];
}) {
  type TaxItem = { id: string; name: string; sortOrder: number };

  const items = React.useMemo<TaxItem[]>(() => {
    let source: TaxItem[] = [];
    if (groupBy === "statusId") source = statuses.map((s) => ({ id: s.id, name: s.name, sortOrder: s.sortOrder }));
    else if (groupBy === "priorityId") source = priorities.map((p) => ({ id: p.id, name: p.name, sortOrder: p.sortOrder }));
    else if (groupBy === "queueId") source = queues.map((q) => ({ id: q.id, name: q.name, sortOrder: q.sortOrder }));
    else if (groupBy === "categoryId") source = categories.map((c) => ({ id: c.id, name: c.name, sortOrder: c.sortOrder }));

    // If groupOrder is set, use it; otherwise sort by taxonomy sort_order
    if (groupOrder.length > 0) {
      const orderIndex = new Map(groupOrder.map((id, i) => [id, i]));
      return [...source].sort((a, b) => {
        const ai = orderIndex.get(a.id) ?? 99999;
        const bi = orderIndex.get(b.id) ?? 99999;
        if (ai !== bi) return ai - bi;
        return a.sortOrder - b.sortOrder;
      });
    }
    return [...source].sort((a, b) => a.sortOrder - b.sortOrder);
  }, [groupBy, groupOrder, statuses, priorities, queues, categories]);

  function move(index: number, dir: -1 | 1) {
    const ids = items.map((i) => i.id);
    const target = index + dir;
    if (target < 0 || target >= ids.length) return;
    [ids[index], ids[target]] = [ids[target], ids[index]];
    onChange(ids);
  }

  function reset() {
    onChange([]);
  }

  if (items.length === 0) return null;

  return (
    <div className="space-y-1.5">
      <div className="flex items-baseline gap-2">
        <span className="text-xs font-medium text-muted-foreground">Group order</span>
        {groupOrder.length > 0 && (
          <button
            type="button"
            onClick={reset}
            className="text-[10px] text-primary/70 hover:text-primary transition-colors"
          >
            Reset to default
          </button>
        )}
      </div>
      <div className="space-y-0.5">
        {items.map((item, i) => (
          <div
            key={item.id}
            className="flex items-center gap-2 rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-1.5 text-sm"
          >
            <span className="flex-1 text-foreground/90">{item.name}</span>
            <button
              type="button"
              onClick={() => move(i, -1)}
              disabled={i === 0}
              className="h-5 w-5 flex items-center justify-center rounded text-muted-foreground hover:text-foreground hover:bg-white/[0.06] disabled:opacity-30 disabled:pointer-events-none transition-colors"
            >
              <ArrowUp className="h-3 w-3" />
            </button>
            <button
              type="button"
              onClick={() => move(i, 1)}
              disabled={i === items.length - 1}
              className="h-5 w-5 flex items-center justify-center rounded text-muted-foreground hover:text-foreground hover:bg-white/[0.06] disabled:opacity-30 disabled:pointer-events-none transition-colors"
            >
              <ArrowDown className="h-3 w-3" />
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

// ---- View dialog (create + edit) ----

type ViewDialogProps = {
  view: View | null;
  queues: Queue[];
  statuses: Status[];
  priorities: Priority[];
  categories: Category[];
  onClose: () => void;
  onSaved: () => void;
};

function ViewDialog({
  view,
  queues,
  statuses,
  priorities,
  categories,
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

  function parseDisplayConfig(json: string | undefined): DisplayConfig {
    if (!json) return {};
    try {
      return JSON.parse(json);
    } catch {
      return {};
    }
  }

  const initial = parseFilters(view?.filtersJson);
  const initialDc = parseDisplayConfig(view?.displayConfigJson);

  function parseColumns(raw: string | null | undefined): string[] {
    if (!raw) return [];
    return raw.split(",").map((c) => c.trim()).filter(Boolean);
  }

  const [name, setName] = React.useState(view?.name ?? "");
  const [filters, setFilters] = React.useState<ViewFilters>(initial);
  const [selectedColumns, setSelectedColumns] = React.useState<string[]>(
    parseColumns(view?.columns),
  );

  // Display config state
  const [priorityFloat, setPriorityFloat] = React.useState(initialDc.priorityFloat ?? false);
  const [groupBy, setGroupBy] = React.useState(initialDc.groupBy ?? "");
  const [groupOrder, setGroupOrder] = React.useState<string[]>(initialDc.groupOrder ?? []);
  const [sortField, setSortField] = React.useState(initialDc.sort?.field ?? "");
  const [sortDirection, setSortDirection] = React.useState<"asc" | "desc">(
    initialDc.sort?.direction ?? "desc",
  );

  // Reset group order when groupBy changes
  React.useEffect(() => {
    setGroupOrder([]);
  }, [groupBy]);

  function toggleColumnSelection(id: string) {
    setSelectedColumns((prev) =>
      prev.includes(id) ? prev.filter((c) => c !== id) : [...prev, id],
    );
  }

  const save = useMutation({
    mutationFn: async () => {
      const dc: DisplayConfig = {};
      if (priorityFloat) dc.priorityFloat = true;
      if (groupBy) dc.groupBy = groupBy;
      if (groupOrder.length > 0) dc.groupOrder = groupOrder;
      if (sortField) dc.sort = { field: sortField, direction: sortDirection };

      const input: ViewInput = {
        name,
        filtersJson: JSON.stringify(filters),
        columns: selectedColumns.length > 0 ? selectedColumns.join(",") : null,
        displayConfigJson: JSON.stringify(dc),
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

  const groupByOption = GROUP_BY_OPTIONS.find((o) => o.value === groupBy);
  const showGroupOrder = !!groupByOption?.hasTaxonomy;

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg max-h-[85vh] overflow-y-auto">
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

          {/* ---- Filters ---- */}
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

          {/* ---- Columns ---- */}
          <div className="space-y-2 pt-1">
            <div className="flex items-baseline gap-2">
              <span className="text-xs font-medium text-muted-foreground">Default columns</span>
              <span className="text-[10px] text-muted-foreground/60">(leave empty to use global default)</span>
            </div>
            <div className="flex flex-wrap gap-1.5">
              {ALL_COLUMNS.map((col) => {
                const active = selectedColumns.includes(col.id);
                return (
                  <button
                    key={col.id}
                    type="button"
                    onClick={() => toggleColumnSelection(col.id)}
                    className={cn(
                      "rounded-full border px-2.5 py-0.5 text-[11px] transition-colors select-none",
                      active
                        ? "border-primary/50 bg-primary/20 text-foreground"
                        : "border-white/10 bg-white/[0.03] text-muted-foreground hover:border-white/20 hover:text-foreground",
                    )}
                  >
                    {col.label}
                  </button>
                );
              })}
            </div>
          </div>

          {/* ---- Display config: Sorting ---- */}
          <div className="space-y-2 pt-2 border-t border-white/[0.06]">
            <span className="text-xs font-medium text-muted-foreground">Sorting</span>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <Field label="Sort by">
                <NativeSelect value={sortField} onChange={setSortField}>
                  <option value="">Default (Updated)</option>
                  {SORT_FIELDS.map((f) => (
                    <option key={f.value} value={f.value}>
                      {f.label}
                    </option>
                  ))}
                </NativeSelect>
              </Field>
              <Field label="Direction">
                <NativeSelect
                  value={sortDirection}
                  onChange={(v) => setSortDirection(v as "asc" | "desc")}
                >
                  <option value="desc">Descending (newest/highest first)</option>
                  <option value="asc">Ascending (oldest/lowest first)</option>
                </NativeSelect>
              </Field>
            </div>
          </div>

          {/* ---- Display config: Grouping ---- */}
          <div className="space-y-2 pt-2 border-t border-white/[0.06]">
            <span className="text-xs font-medium text-muted-foreground">Grouping</span>
            <Field label="Group by">
              <NativeSelect value={groupBy} onChange={setGroupBy}>
                {GROUP_BY_OPTIONS.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </NativeSelect>
            </Field>

            {showGroupOrder && (
              <GroupOrderEditor
                groupBy={groupBy}
                groupOrder={groupOrder}
                onChange={setGroupOrder}
                statuses={statuses}
                priorities={priorities}
                queues={queues}
                categories={categories}
              />
            )}
          </div>

          {/* ---- Display config: Priority float ---- */}
          <div className="flex items-center justify-between pt-2 border-t border-white/[0.06]">
            <div className="space-y-0.5">
              <span className="text-xs font-medium text-muted-foreground">Priority float</span>
              <p className="text-[10px] text-muted-foreground/60 leading-tight">
                Float non-default priority tickets to the top, sorted by priority level
              </p>
            </div>
            <Switch checked={priorityFloat} onCheckedChange={setPriorityFloat} />
          </div>
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
  const filterParts = formatFilters(view.filtersJson, queues, statuses, priorities);
  let dcParts: string[] = [];
  try {
    dcParts = formatDisplayConfig(JSON.parse(view.displayConfigJson || "{}"));
  } catch { /* ignore */ }
  const summaryParts = [...filterParts, ...dcParts];

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
  const navigate = useNavigate();
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

  const { data: categories = [] } = useQuery({
    queryKey: ["taxonomy", "categories"],
    queryFn: () => taxonomyApi.categories.list(),
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
    navigate({ to: "/tickets", search: { viewId: id } });
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
          categories={categories}
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
