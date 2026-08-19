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
import { useProjectSettings } from "@/pages/tickets/components/projects/ProjectPromptDialog";

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
  { id: "pendingTillUtc", label: "Pending till" },
];

// ---- Sort field options ----

const SORT_FIELDS: { value: string; label: string }[] = [
  { value: "updatedUtc", label: "Updated" },
  { value: "createdUtc", label: "Created" },
  { value: "dueUtc", label: "Due date" },
  { value: "pendingTillUtc", label: "Pending till" },
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
//
// v0.0.40 polish — queue / status / priority each accept multiple ids.
// Legacy views written before this change still have the singular
// `queueId` / `statusId` / `priorityId` fields; `normaliseFilters` folds
// those into the array form at read time so no DB migration is needed.

type ViewFilters = {
  queueIds?: string[];
  statusIds?: string[];
  priorityIds?: string[];
  openOnly?: boolean;
  /// v0.0.105 — only project tickets, regardless of queue. Lets one view
  /// collect every project across the whole install.
  projectsOnly?: boolean;
  search?: string;
};

type LegacyViewFilters = ViewFilters & {
  queueId?: string;
  statusId?: string;
  priorityId?: string;
};

function normaliseFilters(raw: LegacyViewFilters): ViewFilters {
  const queueIds = raw.queueIds ?? (raw.queueId ? [raw.queueId] : undefined);
  const statusIds = raw.statusIds ?? (raw.statusId ? [raw.statusId] : undefined);
  const priorityIds =
    raw.priorityIds ?? (raw.priorityId ? [raw.priorityId] : undefined);
  return {
    queueIds: queueIds && queueIds.length > 0 ? queueIds : undefined,
    statusIds: statusIds && statusIds.length > 0 ? statusIds : undefined,
    priorityIds:
      priorityIds && priorityIds.length > 0 ? priorityIds : undefined,
    openOnly: raw.openOnly,
    projectsOnly: raw.projectsOnly,
    search: raw.search,
  };
}

function joinNames<T extends { id: string; name: string }>(
  ids: string[],
  source: T[],
): string {
  return ids
    .map((id) => source.find((s) => s.id === id)?.name)
    .filter((n): n is string => !!n)
    .join(", ");
}

function formatFilters(
  filtersJson: string,
  queues: Queue[],
  statuses: Status[],
  priorities: Priority[],
): string[] {
  try {
    const f = normaliseFilters(JSON.parse(filtersJson) as LegacyViewFilters);
    const parts: string[] = [];
    if (f.queueIds && f.queueIds.length > 0) {
      const names = joinNames(f.queueIds, queues);
      if (names) parts.push(`Queue: ${names}`);
    }
    if (f.statusIds && f.statusIds.length > 0) {
      const names = joinNames(f.statusIds, statuses);
      if (names) parts.push(`Status: ${names}`);
    }
    if (f.priorityIds && f.priorityIds.length > 0) {
      const names = joinNames(f.priorityIds, priorities);
      if (names) parts.push(`Priority: ${names}`);
    }
    if (f.openOnly) parts.push("Open only");
    if (f.projectsOnly) parts.push("Projects only");
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
        "w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground",
        "focus:outline-none focus:ring-1 focus:ring-ring focus:border-glass-strong",
        "disabled:opacity-50",
        "[&_option]:bg-popover [&_option]:text-popover-foreground",
      )}
    >
      {children}
    </select>
  );
}

// ---- MultiSelectPills ----
//
// Chip-toggle grid used by the view editor for Queue / Status / Priority
// filters. An empty selection means "any" (no filter), matching the
// server's behaviour when the array is empty. Mirrors the visual
// language of the column-toggle chips below the filters so the dialog
// reads as one consistent surface.

function MultiSelectPills({
  items,
  selected,
  onChange,
  emptyHint = "Any",
}: {
  items: { id: string; name: string }[];
  selected: string[];
  onChange: (next: string[]) => void;
  emptyHint?: string;
}) {
  function toggle(id: string) {
    onChange(
      selected.includes(id)
        ? selected.filter((s) => s !== id)
        : [...selected, id],
    );
  }

  if (items.length === 0) {
    return (
      <p className="text-[11px] text-muted-foreground/60 italic">
        Nothing to choose from
      </p>
    );
  }

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {selected.length === 0 && (
        <span className="text-[11px] text-muted-foreground/60 italic mr-1">
          {emptyHint}
        </span>
      )}
      {items.map((item) => {
        const active = selected.includes(item.id);
        return (
          <button
            key={item.id}
            type="button"
            onClick={() => toggle(item.id)}
            className={cn(
              "rounded-full border px-2.5 py-0.5 text-[11px] transition-colors select-none",
              active
                ? "border-primary/50 bg-primary/20 text-foreground"
                : "border-glass bg-glass text-muted-foreground hover:border-glass-strong hover:text-foreground",
            )}
          >
            {item.name}
          </button>
        );
      })}
    </div>
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
            className="flex items-center gap-2 rounded-md border border-glass-strong bg-glass px-3 py-1.5 text-sm"
          >
            <span className="flex-1 text-foreground/90">{item.name}</span>
            <button
              type="button"
              onClick={() => move(i, -1)}
              disabled={i === 0}
              className="h-5 w-5 flex items-center justify-center rounded text-muted-foreground hover:text-foreground hover:bg-glass-hover disabled:opacity-30 disabled:pointer-events-none transition-colors"
            >
              <ArrowUp className="h-3 w-3" />
            </button>
            <button
              type="button"
              onClick={() => move(i, 1)}
              disabled={i === items.length - 1}
              className="h-5 w-5 flex items-center justify-center rounded text-muted-foreground hover:text-foreground hover:bg-glass-hover disabled:opacity-30 disabled:pointer-events-none transition-colors"
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
      return normaliseFilters(JSON.parse(json) as LegacyViewFilters);
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
  // v0.0.105 — the "Project tickets only" checkbox renders only while the
  // projects feature is on (an existing stored filter keeps working either
  // way; the server just returns no is_project rows when none exist).
  const projectSettingsQ = useProjectSettings();
  const projectsEnabled = projectSettingsQ.data?.enabled ?? false;
  // Sidebar ordering — lower numbers appear first, identical across users.
  const [sortOrder, setSortOrder] = React.useState<number>(view?.sortOrder ?? 0);
  const [selectedColumns, setSelectedColumns] = React.useState<string[]>(
    parseColumns(view?.columns),
  );

  // Display config state
  const [priorityFloat, setPriorityFloat] = React.useState(initialDc.priorityFloat ?? false);
  const [stateBucketSort, setStateBucketSort] = React.useState(initialDc.stateBucketSort ?? false);
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
      if (stateBucketSort) dc.stateBucketSort = true;
      if (groupBy) dc.groupBy = groupBy;
      if (groupOrder.length > 0) dc.groupOrder = groupOrder;
      if (sortField) dc.sort = { field: sortField, direction: sortDirection };

      const input: ViewInput = {
        name,
        filtersJson: JSON.stringify(filters),
        columns: selectedColumns.length > 0 ? selectedColumns.join(",") : null,
        displayConfigJson: JSON.stringify(dc),
        sortOrder: Math.max(0, Math.min(100, Math.trunc(sortOrder))),
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
          <div className="grid grid-cols-[1fr_7rem] gap-3">
            <Field label="Name">
              <Input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g. My open tickets"
                autoFocus
              />
            </Field>
            <Field label="Priority">
              <Input
                type="number"
                min={0}
                max={100}
                step={1}
                value={sortOrder}
                onChange={(e) => setSortOrder(Number(e.target.value))}
                title="0–100. Lower numbers appear first in the sidebar; identical across users."
              />
            </Field>
          </div>

          {/* ---- Filters ----
              Multi-select chip grids: tap a chip to add/remove, an empty
              selection means "Any" (no filter on that field). */}
          <div className="space-y-3">
            <Field label="Queues">
              <MultiSelectPills
                items={queues.map((q) => ({ id: q.id, name: q.name }))}
                selected={filters.queueIds ?? []}
                onChange={(next) =>
                  patch({ queueIds: next.length > 0 ? next : undefined })
                }
              />
            </Field>

            <Field label="Statuses">
              <MultiSelectPills
                items={statuses.map((s) => ({ id: s.id, name: s.name }))}
                selected={filters.statusIds ?? []}
                onChange={(next) =>
                  patch({ statusIds: next.length > 0 ? next : undefined })
                }
              />
            </Field>

            <Field label="Priorities">
              <MultiSelectPills
                items={priorities.map((p) => ({ id: p.id, name: p.name }))}
                selected={filters.priorityIds ?? []}
                onChange={(next) =>
                  patch({ priorityIds: next.length > 0 ? next : undefined })
                }
              />
            </Field>
          </div>

          <label className="flex items-center gap-2 text-xs text-muted-foreground cursor-pointer select-none">
            <input
              type="checkbox"
              checked={filters.openOnly ?? false}
              onChange={(e) => patch({ openOnly: e.target.checked || undefined })}
              className="rounded border-glass-strong"
            />
            Open only (hide resolved &amp; closed tickets)
          </label>

          {projectsEnabled && (
            <label className="flex items-center gap-2 text-xs text-muted-foreground cursor-pointer select-none">
              <input
                type="checkbox"
                checked={filters.projectsOnly ?? false}
                onChange={(e) => patch({ projectsOnly: e.target.checked || undefined })}
                className="rounded border-glass-strong"
              />
              Project tickets only (every project, regardless of queue)
            </label>
          )}

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
                        : "border-glass bg-glass text-muted-foreground hover:border-glass-strong hover:text-foreground",
                    )}
                  >
                    {col.label}
                  </button>
                );
              })}
            </div>
          </div>

          {/* ---- Display config: Sorting ---- */}
          <div className="space-y-2 pt-2 border-t border-glass-strong">
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
            <div className="flex items-center justify-between pt-1">
              <div className="space-y-0.5">
                <span className="text-xs font-medium text-muted-foreground">Open tickets first</span>
                <p className="text-[10px] text-muted-foreground/60 leading-tight">
                  Sort open tickets above pending (and resolved/closed last) within each group
                </p>
              </div>
              <Switch checked={stateBucketSort} onCheckedChange={setStateBucketSort} />
            </div>
          </div>

          {/* ---- Display config: Grouping ---- */}
          <div className="space-y-2 pt-2 border-t border-glass-strong">
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
          <div className="flex items-center justify-between pt-2 border-t border-glass-strong">
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
    <div className="rounded-lg border border-glass-strong bg-glass transition-colors hover:bg-glass-hover">
      {/* Main bar */}
      <div className="flex items-center gap-3 px-4 py-2.5">
        <button
          type="button"
          onClick={onToggle}
          className="flex h-6 w-6 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-glass-hover transition-colors"
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
          <span
            className="shrink-0 rounded-md border border-glass bg-glass px-1.5 py-0.5 font-mono text-[10px] tabular-nums text-muted-foreground"
            title="Sidebar priority (0 = top, 100 = bottom)"
          >
            {view.sortOrder}
          </span>
          <span className="truncate text-sm font-medium text-foreground">
            {view.name}
          </span>

          {summaryParts.length > 0 && (
            <span className="hidden sm:flex items-center gap-1.5 ml-1">
              {summaryParts.map((part) => (
                <span
                  key={part}
                  className="rounded-full border border-glass bg-glass px-2 py-0.5 text-[10px] text-muted-foreground whitespace-nowrap"
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
        <div className="flex items-center gap-2 border-t border-glass px-4 py-2">
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
                  className="rounded-full border border-glass bg-glass px-2 py-0.5 text-[10px] text-muted-foreground"
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
    <div className="flex items-center gap-3 rounded-lg border border-glass-strong bg-glass px-4 py-2.5">
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

  // Settings → Views is a management surface, so admins see every view in
  // the system, not just the ones they personally have access to.
  const { data: views, isLoading: viewsLoading } = useQuery({
    queryKey: ["views", "all"],
    queryFn: () => viewApi.listAll(),
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
        <div className="rounded-lg border border-glass-strong bg-glass px-6 py-10 flex flex-col items-center justify-center gap-4 text-center">
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
