import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  Check,
  ChevronLeft,
  ChevronRight,
  ClipboardList,
  Columns3,
  ExternalLink,
  GripVertical,
  Pencil,
  Plus,
  RotateCcw,
  Trash2,
  X,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import {
  DndContext,
  PointerSensor,
  KeyboardSensor,
  useSensor,
  useSensors,
  closestCenter,
  type DragEndEvent,
} from "@dnd-kit/core";
import {
  SortableContext,
  arrayMove,
  sortableKeyboardCoordinates,
  verticalListSortingStrategy,
  useSortable,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { cn } from "@/lib/utils";
import { TicketAutocomplete } from "@/components/TicketAutocomplete";
import type { TicketPickerItem } from "@/lib/ticket-api";
import { useServerTime } from "@/hooks/useServerTime";
import { preferencesApi } from "@/lib/api";
import {
  feedbackApi,
  type FeedbackEntryRow,
  type FeedbackEmployee,
  type FeedbackWorkPointType,
  type FeedbackEntryFilter,
  type FeedbackEntryUpdate,
} from "@/lib/api";
import { SafeHtml } from "@/components/SafeHtml";
import { RichTextEditor } from "@/components/RichTextEditor";
import { useAuth } from "@/auth/authStore";
import type { TicketAttachmentMeta } from "@/lib/ticket-api";
import { ApiError } from "@/lib/ticket-api";

// ---- Column definitions ---------------------------------------------------

type ColumnId =
  | "employee"
  | "date"
  | "feedback"
  | "managementRemarks"
  | "workPointType"
  | "completed"
  | "mgmtReviewed"
  | "ticket"
  | "source"
  | "createdBy"
  | "actions";

const ALL_COLUMNS: { id: ColumnId; label: string }[] = [
  { id: "employee", label: "Employee" },
  { id: "date", label: "Date" },
  { id: "feedback", label: "Feedback" },
  { id: "managementRemarks", label: "Management remarks" },
  { id: "workPointType", label: "Work-point type" },
  { id: "completed", label: "Completed" },
  { id: "mgmtReviewed", label: "Mgmt reviewed" },
  { id: "ticket", label: "Ticket" },
  { id: "source", label: "Source" },
  { id: "createdBy", label: "Created by" },
  { id: "actions", label: "Actions" },
];

// Status columns whose cell + header are centered (a lone checkbox).
const CENTERED_COLUMNS: ReadonlySet<ColumnId> = new Set(["completed", "mgmtReviewed"]);

// Default left-to-right order (user-preferred). Differs from ALL_COLUMNS,
// which is just the label registry. Users can drag-reorder in the column
// picker; the saved order overrides this.
const DEFAULT_COLUMN_ORDER: ColumnId[] = [
  "employee",
  "date",
  "ticket",
  "workPointType",
  "feedback",
  "managementRemarks",
  "completed",
  "mgmtReviewed",
  "source",
  "createdBy",
  "actions",
];

const WORKSPACE_KEY_COLS = "feedback.columnOrder";
const WORKSPACE_KEY_VIS = "feedback.columnVisibility";

// ---- Pagination defaults --------------------------------------------------

const PAGE_SIZE = 20;

// ---- Helpers --------------------------------------------------------------

function serverDateToday(time: ReturnType<typeof useServerTime>["time"]): string {
  if (!time) {
    const d = new Date();
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }
  const d = time.serverLocal;
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
}

function stripHtml(html: string | null | undefined): string {
  if (!html) return "";
  return html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
}

// Format a server-set UTC timestamp for a hover tooltip (local display only —
// the value itself is server-authored). Falls back to the raw string.
function formatStamp(utc: string | null | undefined): string {
  if (!utc) return "";
  const d = new Date(utc);
  if (Number.isNaN(d.getTime())) return utc;
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(
    d.getHours(),
  )}:${pad(d.getMinutes())}`;
}

// "<Action> by <who> · <when>" tooltip for a status checkbox; falls back to
// the unticked hint when there is no timestamp yet.
function statusTooltip(
  action: string,
  by: string | null | undefined,
  utc: string | null | undefined,
  untickedHint: string,
): string {
  if (!utc) return untickedHint;
  return `${action} by ${by ?? "?"} · ${formatStamp(utc)}`;
}

// ---- Main page ------------------------------------------------------------

export function FeedbackPage() {
  const qc = useQueryClient();
  const { time } = useServerTime();
  const { user } = useAuth();
  // Full access = the shared board (CRUD every row + management fields).
  // Otherwise the user is in restricted "own-only" mode: they only see rows
  // they logged, and the management fields (remarks + completed) are read-only.
  const fullAccess = user?.feedbackEnabled ?? false;

  // Filter state
  const [filterEmployee, setFilterEmployee] = React.useState<string>("");
  const [filterType, setFilterType] = React.useState<string>("");
  const [filterCompleted, setFilterCompleted] = React.useState<string>("");

  // Edit state
  const [editingId, setEditingId] = React.useState<string | null>(null);

  // Pagination
  const [page, setPage] = React.useState(1);

  // Column order + visibility (persisted via workspace KV)
  const [columnOrder, setColumnOrder] = React.useState<ColumnId[]>(DEFAULT_COLUMN_ORDER);
  const [hiddenColumns, setHiddenColumns] = React.useState<Set<ColumnId>>(new Set());
  const [wsLoaded, setWsLoaded] = React.useState(false);

  // Load workspace prefs once
  React.useEffect(() => {
    preferencesApi
      .getWorkspace()
      .then((ws) => {
        const orderRaw = ws[WORKSPACE_KEY_COLS];
        if (orderRaw) {
          const parsed = orderRaw.split(",").filter((c): c is ColumnId =>
            ALL_COLUMNS.some((col) => col.id === c),
          );
          if (parsed.length > 0) {
            // Ensure any column not in saved order gets appended
            const extra = DEFAULT_COLUMN_ORDER.filter((c) => !parsed.includes(c));
            setColumnOrder([...parsed, ...extra]);
          }
        }
        const visRaw = ws[WORKSPACE_KEY_VIS];
        if (visRaw) {
          const hidden = new Set(
            visRaw.split(",").filter((c): c is ColumnId =>
              ALL_COLUMNS.some((col) => col.id === c),
            ),
          );
          setHiddenColumns(hidden);
        }
      })
      .catch(() => {})
      .finally(() => setWsLoaded(true));
  }, []);

  const saveWorkspace = React.useCallback(
    (order: ColumnId[], hidden: Set<ColumnId>) => {
      preferencesApi
        .saveWorkspace([
          { key: WORKSPACE_KEY_COLS, value: order.join(",") },
          { key: WORKSPACE_KEY_VIS, value: [...hidden].join(",") },
        ])
        .catch(() => {});
    },
    [],
  );

  const toggleColumn = (id: ColumnId) => {
    setHiddenColumns((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      saveWorkspace(columnOrder, next);
      return next;
    });
  };

  const resetColumns = () => {
    const order = DEFAULT_COLUMN_ORDER;
    const hidden = new Set<ColumnId>();
    setColumnOrder(order);
    setHiddenColumns(hidden);
    saveWorkspace(order, hidden);
  };

  // In own-only mode the "created by" column is always the current user, so it
  // carries no information — drop it from the table and the column picker.
  const orderForMode = fullAccess
    ? columnOrder
    : columnOrder.filter((id) => id !== "createdBy");
  const visibleColumns = orderForMode.filter((id) => !hiddenColumns.has(id));

  // Drag-to-reorder columns. Headers are the drag handles; reordering acts on
  // the full canonical order so hidden columns keep their relative position.
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const handleColumnDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const from = columnOrder.indexOf(active.id as ColumnId);
    const to = columnOrder.indexOf(over.id as ColumnId);
    if (from < 0 || to < 0) return;
    const next = arrayMove(columnOrder, from, to);
    setColumnOrder(next);
    saveWorkspace(next, hiddenColumns);
  };

  // Data queries
  const employeesQuery = useQuery({
    queryKey: ["feedback", "employees"],
    queryFn: feedbackApi.listEmployees,
    staleTime: 60_000,
  });

  const typesQuery = useQuery({
    queryKey: ["feedback", "work-point-types"],
    queryFn: feedbackApi.listWorkPointTypes,
    staleTime: 60_000,
  });

  const filter: FeedbackEntryFilter = React.useMemo(() => {
    const f: FeedbackEntryFilter = {};
    if (filterEmployee) f.targetUserId = filterEmployee;
    if (filterType) f.workPointTypeId = filterType;
    if (filterCompleted === "true") f.completed = true;
    if (filterCompleted === "false") f.completed = false;
    return f;
  }, [filterEmployee, filterType, filterCompleted]);

  const entriesQuery = useQuery({
    queryKey: ["feedback", "entries", filter],
    queryFn: () => feedbackApi.listEntries(filter),
  });

  const entries = entriesQuery.data?.items ?? [];
  const employees = employeesQuery.data?.items ?? [];
  const workPointTypes = typesQuery.data?.items ?? [];

  // Pagination
  const totalPages = Math.max(1, Math.ceil(entries.length / PAGE_SIZE));
  const pagedEntries = entries.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);

  React.useEffect(() => {
    if (page > totalPages) setPage(totalPages);
  }, [page, totalPages]);

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["feedback", "entries"] });
  };

  // Add row mutation
  const addMutation = useMutation({
    mutationFn: () => feedbackApi.createEntry(),
    onSuccess: (row) => {
      invalidate();
      setEditingId(row.id);
      setPage(1); // new rows come back first? Actually backend may sort by date desc — we don't know, just reset to page 1
      toast.success("New feedback entry added.");
    },
    onError: () => {
      toast.error("Could not create feedback entry.");
    },
  });

  const clearFilters = () => {
    setFilterEmployee("");
    setFilterType("");
    setFilterCompleted("");
    setPage(1);
    setEditingId(null);
  };

  const hasFilters = filterEmployee || filterType || filterCompleted;

  if (!wsLoaded) {
    return (
      <div className="flex flex-col gap-4 p-6">
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4 p-6">
      {/* Header */}
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <div className="mb-2 text-primary">
            <ClipboardList className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Employee Feedback</h1>
          <p className="text-sm text-muted-foreground">
            {fullAccess
              ? "Inline-editable feedback entries per employee with work-point types and ticket links."
              : "Feedback you logged. You only see your own entries; management fields are read-only."}
          </p>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <ColumnSelectorDropdown
            columnOrder={orderForMode}
            hiddenColumns={hiddenColumns}
            onToggle={toggleColumn}
            onReset={resetColumns}
            sensors={sensors}
            onDragEnd={handleColumnDragEnd}
          />
          <Button
            size="sm"
            onClick={() => addMutation.mutate()}
            disabled={addMutation.isPending}
            className="h-8"
          >
            <Plus className="mr-1.5 h-3.5 w-3.5" />
            Add feedback
          </Button>
        </div>
      </header>

      {/* Filter bar */}
      <div className="glass-panel flex flex-wrap items-end gap-3 p-3">
        <FieldGroup label="Employee">
          <select
            value={filterEmployee}
            onChange={(e) => { setFilterEmployee(e.target.value); setPage(1); setEditingId(null); }}
            className={SELECT_CLASS}
          >
            <option value="">All employees</option>
            {employees.map((e) => (
              <option key={e.id} value={e.id}>{e.email}</option>
            ))}
          </select>
        </FieldGroup>
        <FieldGroup label="Work-point type">
          <select
            value={filterType}
            onChange={(e) => { setFilterType(e.target.value); setPage(1); setEditingId(null); }}
            className={SELECT_CLASS}
          >
            <option value="">All types</option>
            {workPointTypes.map((t) => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
        </FieldGroup>
        <FieldGroup label="Completed">
          <select
            value={filterCompleted}
            onChange={(e) => { setFilterCompleted(e.target.value); setPage(1); setEditingId(null); }}
            className={SELECT_CLASS}
          >
            <option value="">All</option>
            <option value="false">Not completed</option>
            <option value="true">Completed</option>
          </select>
        </FieldGroup>
        {hasFilters && (
          <Button size="sm" variant="ghost" onClick={clearFilters} className="h-8">
            <X className="mr-1.5 h-3.5 w-3.5" />
            Clear filters
          </Button>
        )}
        <div className="ml-auto text-xs text-muted-foreground">
          {entriesQuery.isFetching ? "Loading…" : `${entries.length} ${entries.length === 1 ? "entry" : "entries"}`}
        </div>
      </div>

      {/* Table */}
      <section className="glass-card overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
              <tr>
                {visibleColumns.map((col) => (
                  <th
                    key={col}
                    className={cn(
                      "px-3 py-2 font-medium whitespace-nowrap",
                      CENTERED_COLUMNS.has(col) && "text-center",
                    )}
                  >
                    {ALL_COLUMNS.find((c) => c.id === col)!.label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {entriesQuery.isLoading && (
                <tr>
                  <td colSpan={visibleColumns.length} className="px-3 py-3">
                    <Skeleton className="h-6 w-full" />
                  </td>
                </tr>
              )}
              {!entriesQuery.isLoading && pagedEntries.length === 0 && (
                <tr>
                  <td
                    colSpan={visibleColumns.length}
                    className="px-3 py-8 text-center text-sm text-muted-foreground"
                  >
                    No entries match these filters.
                  </td>
                </tr>
              )}
              {!entriesQuery.isLoading &&
                pagedEntries.map((entry) =>
                  editingId === entry.id ? (
                    <EditableRow
                      key={entry.id}
                      entry={entry}
                      employees={employees}
                      workPointTypes={workPointTypes}
                      visibleColumns={visibleColumns}
                      serverToday={serverDateToday(time)}
                      readOnlyManagement={!fullAccess}
                      onCancel={() => setEditingId(null)}
                      onSaved={() => {
                        setEditingId(null);
                        invalidate();
                      }}
                    />
                  ) : (
                    <DisplayRow
                      key={entry.id}
                      entry={entry}
                      visibleColumns={visibleColumns}
                      readOnlyManagement={!fullAccess}
                      onEdit={() => setEditingId(entry.id)}
                      onDeleted={() => invalidate()}
                      onCompletedToggled={() => invalidate()}
                    />
                  ),
                )}
            </tbody>
          </table>
        </div>
      </section>

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between text-xs text-muted-foreground">
          <span>
            Showing {(page - 1) * PAGE_SIZE + 1}–{Math.min(page * PAGE_SIZE, entries.length)} of {entries.length}
          </span>
          <div className="flex items-center gap-1">
            <button
              type="button"
              disabled={page <= 1}
              onClick={() => setPage((p) => p - 1)}
              className="flex h-7 w-7 items-center justify-center rounded-md border border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground disabled:opacity-40"
            >
              <ChevronLeft className="h-3.5 w-3.5" />
            </button>
            <span className="px-2">
              {page} / {totalPages}
            </span>
            <button
              type="button"
              disabled={page >= totalPages}
              onClick={() => setPage((p) => p + 1)}
              className="flex h-7 w-7 items-center justify-center rounded-md border border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground disabled:opacity-40"
            >
              <ChevronRight className="h-3.5 w-3.5" />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

// ---- Shared select class (glass style matching TicketFilters) -------------

const SELECT_CLASS =
  "h-8 rounded-md border border-glass bg-glass px-2 text-sm text-foreground focus:outline-none focus:ring-2 focus:ring-ring min-w-[10rem]";

// ---- Field group label ----------------------------------------------------

function FieldGroup({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[10px] font-medium uppercase tracking-widest text-muted-foreground/70">
        {label}
      </span>
      {children}
    </div>
  );
}

// ---- Column selector dropdown (toggle visibility + drag to reorder) -------

function ColumnSelectorDropdown({
  columnOrder,
  hiddenColumns,
  onToggle,
  onReset,
  sensors,
  onDragEnd,
}: {
  columnOrder: ColumnId[];
  hiddenColumns: Set<ColumnId>;
  onToggle: (id: ColumnId) => void;
  onReset: () => void;
  sensors: ReturnType<typeof useSensors>;
  onDragEnd: (event: DragEndEvent) => void;
}) {
  const [open, setOpen] = React.useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="flex items-center gap-1.5 h-8 px-3 rounded-md border border-glass bg-glass text-sm text-muted-foreground hover:bg-glass-hover hover:text-foreground transition-colors"
        >
          <Columns3 className="h-3.5 w-3.5" />
          Columns
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-60 p-2" align="end">
        <p className="px-2 pb-1.5 text-[10px] uppercase tracking-widest text-muted-foreground/70">
          Drag to reorder · tick to show
        </p>
        <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
          <SortableContext items={columnOrder} strategy={verticalListSortingStrategy}>
            <div className="space-y-0.5">
              {columnOrder.map((id) => (
                <ColumnPickerRow
                  key={id}
                  id={id}
                  label={ALL_COLUMNS.find((c) => c.id === id)!.label}
                  // "actions" must always be visible — the row can be
                  // reordered but its checkbox is locked on.
                  checked={id === "actions" ? true : !hiddenColumns.has(id)}
                  lockedOn={id === "actions"}
                  onToggle={() => onToggle(id)}
                />
              ))}
            </div>
          </SortableContext>
        </DndContext>
        <div className="mt-2 border-t border-glass pt-2">
          <button
            type="button"
            onClick={() => { onReset(); setOpen(false); }}
            className="flex w-full items-center gap-1.5 rounded px-2 py-1.5 text-xs text-muted-foreground hover:text-foreground hover:bg-glass-hover transition-colors"
          >
            <RotateCcw className="h-3 w-3" />
            Reset to defaults
          </button>
        </div>
      </PopoverContent>
    </Popover>
  );
}

function ColumnPickerRow({
  id,
  label,
  checked,
  lockedOn,
  onToggle,
}: {
  id: ColumnId;
  label: string;
  checked: boolean;
  lockedOn: boolean;
  onToggle: () => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } =
    useSortable({ id });
  const style: React.CSSProperties = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.6 : 1,
  };
  return (
    <div
      ref={setNodeRef}
      style={style}
      className={cn(
        "flex items-center gap-2 rounded px-1.5 py-1.5 text-sm",
        isDragging ? "bg-glass-hover" : "hover:bg-glass-hover",
      )}
    >
      <button
        type="button"
        className="cursor-grab text-muted-foreground/60 hover:text-foreground touch-none"
        aria-label={`Reorder ${label}`}
        {...attributes}
        {...listeners}
      >
        <GripVertical className="h-3.5 w-3.5" />
      </button>
      <input
        type="checkbox"
        checked={checked}
        disabled={lockedOn}
        onChange={onToggle}
        className="rounded border-glass-strong bg-glass accent-primary disabled:opacity-50"
      />
      <span className={checked ? "text-foreground" : "text-muted-foreground"}>{label}</span>
    </div>
  );
}

// ---- Display row ----------------------------------------------------------

function DisplayRow({
  entry,
  visibleColumns,
  readOnlyManagement,
  onEdit,
  onDeleted,
  onCompletedToggled,
}: {
  entry: FeedbackEntryRow;
  visibleColumns: ColumnId[];
  readOnlyManagement: boolean;
  onEdit: () => void;
  onDeleted: () => void;
  onCompletedToggled: () => void;
}) {
  const qc = useQueryClient();

  const completedMutation = useMutation({
    mutationFn: (completed: boolean) => feedbackApi.setCompleted(entry.id, completed),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["feedback", "entries"] });
      onCompletedToggled();
    },
    onError: () => toast.error("Could not update completion status."),
  });

  const mgmtReviewedMutation = useMutation({
    mutationFn: (reviewed: boolean) => feedbackApi.setMgmtReviewed(entry.id, reviewed),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["feedback", "entries"] });
      onCompletedToggled();
    },
    onError: () => toast.error("Could not update review status."),
  });

  const deleteMutation = useMutation({
    mutationFn: () => feedbackApi.deleteEntry(entry.id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["feedback", "entries"] });
      toast.success("Entry deleted.");
      onDeleted();
    },
    onError: () => toast.error("Could not delete entry."),
  });

  const [deleteConfirm, setDeleteConfirm] = React.useState(false);

  const cells: Record<ColumnId, React.ReactNode> = {
    employee: (
      <span className="text-foreground">
        {entry.targetUserEmail ?? <span className="text-muted-foreground italic">Unassigned</span>}
      </span>
    ),
    date: (
      <span className="text-foreground font-mono text-xs">{entry.entryDate?.slice(0, 10)}</span>
    ),
    feedback: (
      <RichTextPreviewCell
        html={entry.bodyHtml}
        label="Feedback"
        dialogTitle="Feedback"
      />
    ),
    managementRemarks: (
      <RichTextPreviewCell
        html={entry.managementRemarksHtml}
        label="Management remarks"
        dialogTitle="Management remarks"
      />
    ),
    workPointType: entry.workPointTypeId ? (
      <WorkPointTypeBadge
        name={entry.workPointTypeName ?? ""}
        color={entry.workPointTypeColor ?? "#888"}
      />
    ) : (
      <span className="text-muted-foreground italic text-xs">—</span>
    ),
    completed: (
      <div className="flex justify-center">
      <input
        type="checkbox"
        checked={entry.isCompleted}
        disabled={readOnlyManagement || completedMutation.isPending}
        onChange={() => completedMutation.mutate(!entry.isCompleted)}
        className="rounded border-glass-strong bg-glass accent-primary disabled:opacity-60"
        title={statusTooltip(
          "Completed",
          entry.completedByEmail,
          entry.completedUtc,
          readOnlyManagement ? "Not completed" : "Mark as completed",
        )}
      />
      </div>
    ),
    mgmtReviewed: (
      <div className="flex justify-center">
        <input
          type="checkbox"
          checked={entry.mgmtReviewed}
          disabled={readOnlyManagement || mgmtReviewedMutation.isPending}
          onChange={() => mgmtReviewedMutation.mutate(!entry.mgmtReviewed)}
          className="rounded border-glass-strong bg-glass accent-primary disabled:opacity-60"
          title={statusTooltip(
            "Reviewed",
            entry.mgmtReviewedByEmail,
            entry.mgmtReviewedUtc,
            readOnlyManagement ? "Not reviewed" : "Mark as reviewed by management",
          )}
        />
      </div>
    ),
    ticket: entry.linkedTicketId ? (
      <a
        href={
          entry.linkedTicketEventId
            ? `/tickets/${entry.linkedTicketId}#event-${entry.linkedTicketEventId}`
            : `/tickets/${entry.linkedTicketId}`
        }
        target="_blank"
        rel="noreferrer"
        className="inline-flex items-center gap-1 text-primary hover:underline text-xs"
        title={
          entry.linkedTicketEventId
            ? "Open the logged note/reply/email in a new tab"
            : "Open ticket in a new tab"
        }
      >
        #{entry.linkedTicketNumber}
        <ExternalLink className="h-3 w-3" />
      </a>
    ) : entry.linkedTicketNumber ? (
      <span className="text-xs text-muted-foreground">
        #{entry.linkedTicketNumber}
      </span>
    ) : (
      <span className="text-muted-foreground italic text-xs">—</span>
    ),
    source:
      entry.source === "activity" ? (
        <span className="inline-flex items-center rounded-full border border-primary/40 bg-primary/10 px-2 py-0.5 text-[10px] font-medium text-primary">
          From activity
        </span>
      ) : (
        <span className="inline-flex items-center rounded-full border border-glass bg-glass px-2 py-0.5 text-[10px] font-medium text-muted-foreground">
          Manual
        </span>
      ),
    createdBy: (
      <span className="text-xs text-muted-foreground">
        {entry.createdByEmail ?? "—"}
      </span>
    ),
    actions: (
      <div className="flex items-center gap-1">
        <button
          type="button"
          title="Edit"
          onClick={onEdit}
          className="flex h-7 w-7 items-center justify-center rounded-md border border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground transition-colors"
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
        {deleteConfirm ? (
          <>
            <button
              type="button"
              title="Confirm delete"
              onClick={() => deleteMutation.mutate()}
              disabled={deleteMutation.isPending}
              className="flex h-7 w-7 items-center justify-center rounded-md border border-destructive bg-destructive/10 text-destructive hover:bg-destructive/20 transition-colors"
            >
              <Check className="h-3.5 w-3.5" />
            </button>
            <button
              type="button"
              title="Cancel"
              onClick={() => setDeleteConfirm(false)}
              className="flex h-7 w-7 items-center justify-center rounded-md border border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground transition-colors"
            >
              <X className="h-3.5 w-3.5" />
            </button>
          </>
        ) : (
          <button
            type="button"
            title="Delete"
            onClick={() => setDeleteConfirm(true)}
            className="flex h-7 w-7 items-center justify-center rounded-md border border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-destructive transition-colors"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </button>
        )}
      </div>
    ),
  };

  return (
    <tr className="border-b border-glass hover:bg-glass-hover transition-colors">
      {visibleColumns.map((col) => (
        <td key={col} className="px-3 py-2 align-middle">
          {cells[col]}
        </td>
      ))}
    </tr>
  );
}

// ---- Editable row ---------------------------------------------------------

function EditableRow({
  entry,
  employees,
  workPointTypes,
  visibleColumns,
  serverToday,
  readOnlyManagement,
  onCancel,
  onSaved,
}: {
  entry: FeedbackEntryRow;
  employees: FeedbackEmployee[];
  workPointTypes: FeedbackWorkPointType[];
  visibleColumns: ColumnId[];
  serverToday: string;
  readOnlyManagement: boolean;
  onCancel: () => void;
  onSaved: () => void;
}) {
  const [targetUserId, setTargetUserId] = React.useState<string>(entry.targetUserId ?? "");
  // entryDate arrives as an ISO datetime ("2026-06-16T00:00:00"); a date input
  // only accepts YYYY-MM-DD. New rows already carry today's date (DB default).
  const [entryDate, setEntryDate] = React.useState<string>(
    entry.entryDate ? entry.entryDate.slice(0, 10) : serverToday,
  );
  const [bodyHtml, setBodyHtml] = React.useState<string>(entry.bodyHtml ?? "");
  const [managementRemarksHtml, setManagementRemarksHtml] = React.useState<string>(
    entry.managementRemarksHtml ?? "",
  );
  const [workPointTypeId, setWorkPointTypeId] = React.useState<string>(
    entry.workPointTypeId ?? "",
  );
  // Forced selection via the ticket picker (no free text). Seed from the saved
  // link; only number/subject are shown so a partial object is sufficient.
  const [ticket, setTicket] = React.useState<TicketPickerItem | null>(
    entry.linkedTicketId && entry.linkedTicketNumber != null
      ? ({
          id: entry.linkedTicketId,
          number: entry.linkedTicketNumber,
          subject: entry.linkedTicketSubject ?? "",
        } as TicketPickerItem)
      : null,
  );
  const [isCompleted, setIsCompleted] = React.useState(entry.isCompleted);
  const [isMgmtReviewed, setIsMgmtReviewed] = React.useState(entry.mgmtReviewed);

  // Dialog open state for each rich-text field
  const [feedbackOpen, setFeedbackOpen] = React.useState(false);
  const [remarksOpen, setRemarksOpen] = React.useState(false);

  const [fieldErrors, setFieldErrors] = React.useState<Record<string, string>>({});
  const [saving, setSaving] = React.useState(false);

  const handleSave = async () => {
    setSaving(true);
    setFieldErrors({});
    const payload: FeedbackEntryUpdate = {
      targetUserId: targetUserId || null,
      entryDate,
      bodyHtml: bodyHtml || null,
      managementRemarksHtml: managementRemarksHtml || null,
      workPointTypeId: workPointTypeId || null,
      isCompleted,
      isMgmtReviewed,
      linkedTicketNumber: ticket ? ticket.number : null,
    };
    try {
      await feedbackApi.updateEntry(entry.id, payload);
      toast.success("Feedback entry saved.");
      onSaved();
    } catch (err) {
      if (err instanceof ApiError && err.status === 422) {
        const body = err.body as { errors?: { field: string; message: string }[] } | null;
        const errors: Record<string, string> = {};
        for (const e of body?.errors ?? []) {
          errors[e.field] = e.message;
        }
        setFieldErrors(errors);
        toast.error("Please fix the validation errors.");
      } else {
        toast.error("Could not save feedback entry.");
      }
    } finally {
      setSaving(false);
    }
  };

  // Upload handler for inline images
  const makeUploadHandler = () => async (file: File): Promise<TicketAttachmentMeta | null> => {
    try {
      const meta = await feedbackApi.uploadAttachment(entry.id, file);
      return {
        id: meta.id,
        url: meta.url,
        mimeType: meta.mimeType,
        size: meta.size,
        filename: meta.filename,
      };
    } catch (e) {
      const err = e as Error & { payload?: { error?: string } };
      toast.error(err.payload?.error ?? "Upload failed.");
      return null;
    }
  };

  const cells: Record<ColumnId, React.ReactNode> = {
    employee: (
      <div className="flex flex-col gap-1">
        <select
          value={targetUserId}
          onChange={(e) => setTargetUserId(e.target.value)}
          className={cn(SELECT_CLASS, "min-w-[10rem]")}
        >
          <option value="">— Unassigned —</option>
          {employees.map((emp) => (
            <option key={emp.id} value={emp.id}>{emp.email}</option>
          ))}
        </select>
        {fieldErrors.targetUserId && (
          <span className="text-xs text-destructive">{fieldErrors.targetUserId}</span>
        )}
      </div>
    ),
    date: (
      <div className="flex flex-col gap-1">
        <input
          type="date"
          value={entryDate}
          onChange={(e) => setEntryDate(e.target.value)}
          className="h-8 rounded-md border border-glass bg-glass px-2 text-sm text-foreground focus:outline-none focus:ring-2 focus:ring-ring"
        />
        {fieldErrors.entryDate && (
          <span className="text-xs text-destructive">{fieldErrors.entryDate}</span>
        )}
      </div>
    ),
    feedback: (
      <RichTextCellEditor
        label="Feedback"
        html={bodyHtml}
        onChange={setBodyHtml}
        open={feedbackOpen}
        onOpen={() => setFeedbackOpen(true)}
        onClose={() => setFeedbackOpen(false)}
        onUploadFile={makeUploadHandler()}
      />
    ),
    // Management remarks are read-only for restricted users — show the same
    // preview the display row uses instead of the editor.
    managementRemarks: readOnlyManagement ? (
      <RichTextPreviewCell
        html={managementRemarksHtml}
        label="Management remarks"
        dialogTitle="Management remarks"
      />
    ) : (
      <RichTextCellEditor
        label="Management remarks"
        html={managementRemarksHtml}
        onChange={setManagementRemarksHtml}
        open={remarksOpen}
        onOpen={() => setRemarksOpen(true)}
        onClose={() => setRemarksOpen(false)}
        onUploadFile={makeUploadHandler()}
      />
    ),
    workPointType: (
      <select
        value={workPointTypeId}
        onChange={(e) => setWorkPointTypeId(e.target.value)}
        className={cn(SELECT_CLASS, "min-w-[9rem]")}
      >
        <option value="">— None —</option>
        {workPointTypes.map((t) => (
          <option key={t.id} value={t.id}>{t.name}</option>
        ))}
      </select>
    ),
    completed: (
      <div className="flex justify-center">
        <input
          type="checkbox"
          checked={isCompleted}
          disabled={readOnlyManagement}
          onChange={(e) => setIsCompleted(e.target.checked)}
          className="rounded border-glass-strong bg-glass accent-primary disabled:opacity-60"
          title={statusTooltip(
            "Completed",
            entry.completedByEmail,
            entry.completedUtc,
            readOnlyManagement ? "Not completed" : "Mark as completed",
          )}
        />
      </div>
    ),
    mgmtReviewed: (
      <div className="flex justify-center">
        <input
          type="checkbox"
          checked={isMgmtReviewed}
          disabled={readOnlyManagement}
          onChange={(e) => setIsMgmtReviewed(e.target.checked)}
          className="rounded border-glass-strong bg-glass accent-primary disabled:opacity-60"
          title={statusTooltip(
            "Reviewed",
            entry.mgmtReviewedByEmail,
            entry.mgmtReviewedUtc,
            readOnlyManagement ? "Not reviewed" : "Mark as reviewed by management",
          )}
        />
      </div>
    ),
    ticket: (
      <div className="min-w-[12rem]">
        <TicketAutocomplete
          value={ticket}
          disabled={false}
          onChange={setTicket}
          error={fieldErrors.linkedTicketNumber}
        />
      </div>
    ),
    // Read-only in edit mode (set by the system / on creation).
    source:
      entry.source === "activity" ? (
        <span className="inline-flex items-center rounded-full border border-primary/40 bg-primary/10 px-2 py-0.5 text-[10px] font-medium text-primary">
          From activity
        </span>
      ) : (
        <span className="inline-flex items-center rounded-full border border-glass bg-glass px-2 py-0.5 text-[10px] font-medium text-muted-foreground">
          Manual
        </span>
      ),
    createdBy: (
      <span className="text-xs text-muted-foreground">{entry.createdByEmail ?? "—"}</span>
    ),
    actions: (
      <div className="flex items-center gap-1">
        <button
          type="button"
          title="Save"
          onClick={handleSave}
          disabled={saving}
          className="flex h-7 w-7 items-center justify-center rounded-md border border-primary/40 bg-primary/10 text-primary hover:bg-primary/20 transition-colors disabled:opacity-50"
        >
          <Check className="h-3.5 w-3.5" />
        </button>
        <button
          type="button"
          title="Cancel"
          onClick={onCancel}
          disabled={saving}
          className="flex h-7 w-7 items-center justify-center rounded-md border border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground transition-colors"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>
    ),
  };

  return (
    <tr className="border-b border-glass bg-glass">
      {visibleColumns.map((col) => (
        <td key={col} className="px-3 py-2 align-top">
          {cells[col]}
        </td>
      ))}
    </tr>
  );
}

// ---- Rich-text preview cell (display mode) --------------------------------

function RichTextPreviewCell({
  html,
  label,
  dialogTitle,
}: {
  html: string | null | undefined;
  label: string;
  dialogTitle: string;
}) {
  const [open, setOpen] = React.useState(false);
  const preview = stripHtml(html);

  if (!preview) {
    return <span className="text-muted-foreground italic text-xs">—</span>;
  }

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title={`View ${label}`}
        className="max-w-[14rem] truncate text-left text-xs text-foreground/80 hover:text-foreground hover:underline cursor-pointer"
      >
        {preview.slice(0, 80)}{preview.length > 80 ? "…" : ""}
      </button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-w-2xl max-h-[90vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>{dialogTitle}</DialogTitle>
          </DialogHeader>
          <SafeHtml html={html ?? ""} />
        </DialogContent>
      </Dialog>
    </>
  );
}

// ---- Rich-text cell editor (edit mode) ------------------------------------

function RichTextCellEditor({
  label,
  html,
  onChange,
  open,
  onOpen,
  onClose,
  onUploadFile,
}: {
  label: string;
  html: string;
  onChange: (html: string) => void;
  open: boolean;
  onOpen: () => void;
  onClose: () => void;
  onUploadFile: (file: File) => Promise<TicketAttachmentMeta | null>;
}) {
  const preview = stripHtml(html);

  return (
    <>
      <button
        type="button"
        onClick={onOpen}
        className={cn(
          "flex h-8 min-w-[10rem] max-w-[16rem] items-center rounded-md border border-glass bg-glass px-2 text-left text-sm transition-colors hover:bg-glass-hover",
          preview ? "text-foreground" : "text-muted-foreground italic",
        )}
      >
        <span className="truncate">{preview ? preview.slice(0, 60) : `Edit ${label}…`}</span>
      </button>
      <Dialog open={open} onOpenChange={(v) => (v ? onOpen() : onClose())}>
        <DialogContent className="max-w-3xl w-full">
          <DialogHeader>
            <DialogTitle>{label}</DialogTitle>
          </DialogHeader>
          {/* Resizable: drag the bottom-right handle to grow/shrink the typing
              area. The wrapper owns the height + scroll; the editor's own cap is
              disabled (huge maxHeight) so there's no nested scrollbar. */}
          <div className="resize-y overflow-auto min-h-[220px] max-h-[80vh] h-[44vh]">
            <RichTextEditor
              content={html}
              onChange={onChange}
              placeholder={`Enter ${label.toLowerCase()}…`}
              minHeight="180px"
              maxHeight="100000px"
              linkNonImageUploads
              onUploadFile={onUploadFile}
            />
          </div>
          <div className="flex justify-end pt-2 border-t border-glass">
            <Button size="sm" onClick={onClose}>
              Done
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}

// ---- Work-point type badge ------------------------------------------------

function WorkPointTypeBadge({ name, color }: { name: string; color: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-xs">
      <span
        className="inline-block h-2.5 w-2.5 rounded-full shrink-0"
        style={{ backgroundColor: color }}
      />
      {name}
    </span>
  );
}
