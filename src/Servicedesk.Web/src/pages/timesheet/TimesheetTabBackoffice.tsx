import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  CheckCheck,
  ChevronLeft,
  ChevronRight,
  Columns3,
  GripVertical,
  Inbox,
  Settings2,
} from "lucide-react";
import {
  DndContext,
  PointerSensor,
  closestCenter,
  useSensor,
  useSensors,
  type DragEndEvent,
} from "@dnd-kit/core";
import {
  SortableContext,
  arrayMove,
  useSortable,
  verticalListSortingStrategy,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import {
  backofficeTimesheetApi,
  preferencesApi,
  type BackofficeContext,
  type BackofficeTicket,
} from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";

type BoFilter = "all" | "checked" | "unchecked";

// ---- formatting helpers ----------------------------------------------

/// Minutes → "3u 30m" (or "45m" / "2u"). Empty marker for null/0.
function formatMinutes(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined || minutes <= 0) return "—";
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}u`;
  return `${h}u ${m}m`;
}

function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  try {
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}

// ---- column model -----------------------------------------------------

type ColId = "number" | "subject" | "customer" | "hours" | "bochecked";

type ColMeta = {
  id: ColId;
  label: string;
  align: "left" | "right";
  /// Cell holds an interactive control (pill / checkbox) — clicks inside it
  /// must not bubble to row-level handlers.
  interactive?: boolean;
};

const ALL_COLUMNS: ColMeta[] = [
  { id: "number", label: "Ticket", align: "left" },
  { id: "subject", label: "Title", align: "left" },
  { id: "customer", label: "Customer", align: "left" },
  { id: "hours", label: "Hours", align: "right", interactive: true },
  { id: "bochecked", label: "BO checked", align: "right", interactive: true },
];

const COL_LABELS: Record<ColId, string> = Object.fromEntries(
  ALL_COLUMNS.map((c) => [c.id, c.label]),
) as Record<ColId, string>;

type ColConfig = { id: ColId; visible: boolean };

const DEFAULT_CONFIG: ColConfig[] = ALL_COLUMNS.map((c) => ({ id: c.id, visible: true }));

/// Merge a saved config with ALL_COLUMNS: keep known ids in saved order, drop
/// unknown ids, append columns added since the config was saved (visible) so
/// a new column never silently disappears.
function mergeConfig(raw: string | undefined): ColConfig[] {
  if (!raw) return DEFAULT_CONFIG;
  let saved: ColConfig[];
  try {
    const parsed = JSON.parse(raw) as ColConfig[];
    if (!Array.isArray(parsed)) return DEFAULT_CONFIG;
    saved = parsed.filter((c) => ALL_COLUMNS.some((d) => d.id === c.id));
  } catch {
    return DEFAULT_CONFIG;
  }
  const seen = new Set(saved.map((c) => c.id));
  const merged = saved.map((c) => ({ id: c.id, visible: c.visible !== false }));
  for (const d of ALL_COLUMNS) {
    if (!seen.has(d.id)) merged.push({ id: d.id, visible: true });
  }
  return merged.length > 0 ? merged : DEFAULT_CONFIG;
}

// ---- main tab ---------------------------------------------------------

/// Back-office Resolved / CWI tab. The two tabs share this component and
/// differ only by `context`:
///   - "resolved" → tickets in the configured Resolved statuses that have no
///     Adsolut sales receipt yet.
///   - "cwi" → tickets in the configured CWI (Closed Without Invoice)
///     statuses.
/// Both list the tickets that entered one of those statuses in the picked
/// month, with ticket number, title, customer, total registered hours (a
/// clickable pill with a per-task breakdown) and a per-tab "BO checked"
/// marker. Columns are per-user configurable (visibility + order); rows can
/// be bulk-(un)checked.
export function TimesheetTabBackoffice({ context }: { context: BackofficeContext }) {
  const qc = useQueryClient();
  const prefKey = `workspace:backoffice-${context}-columns`;
  const listKey = ["timesheet", "backoffice", context] as const;

  const [{ year, month }, setYM] = useState(() => {
    const d = new Date();
    return { year: d.getFullYear(), month: d.getMonth() + 1 };
  });
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [boFilter, setBoFilter] = useState<BoFilter>("all");

  // Per-agent column config — load once from workspace prefs, own locally.
  const wsQuery = useQuery({
    queryKey: ["preferences", "workspace"],
    queryFn: () => preferencesApi.getWorkspace(),
    staleTime: 60_000,
  });
  const [config, setConfig] = useState<ColConfig[] | null>(null);
  useEffect(() => {
    if (config !== null || wsQuery.data === undefined) return;
    setConfig(mergeConfig(wsQuery.data[prefKey]));
  }, [wsQuery.data, config, prefKey]);
  const effectiveConfig = config ?? DEFAULT_CONFIG;

  const persistConfig = (next: ColConfig[]) => {
    setConfig(next);
    preferencesApi.saveWorkspace([{ key: prefKey, value: JSON.stringify(next) }]).catch(() => {
      /* best-effort */
    });
  };

  const visibleColumns = useMemo(
    () =>
      effectiveConfig
        .filter((c) => c.visible)
        .map((c) => ALL_COLUMNS.find((d) => d.id === c.id))
        .filter((d): d is ColMeta => d !== undefined),
    [effectiveConfig],
  );

  const list = useQuery({
    queryKey: [...listKey, year, month],
    queryFn: () =>
      context === "resolved"
        ? backofficeTimesheetApi.listResolved(year, month)
        : backofficeTimesheetApi.listCwi(year, month),
  });

  // Reset the selection whenever the underlying set of rows changes.
  useEffect(() => {
    setSelected(new Set());
  }, [year, month, context]);

  const setChecks = useMutation({
    mutationFn: (vars: { ids: string[]; checked: boolean }) =>
      backofficeTimesheetApi.setChecks(context, vars.ids, vars.checked),
    onSuccess: (_res, vars) => {
      qc.invalidateQueries({ queryKey: listKey });
      toast.success(
        vars.checked
          ? `${vars.ids.length} marked BO checked`
          : `${vars.ids.length} unmarked`,
      );
    },
    onError: () => toast.error("Could not update BO check"),
  });

  const allItems = list.data?.items ?? [];
  const items = useMemo(
    () =>
      allItems.filter((r) =>
        boFilter === "all" ? true : boFilter === "checked" ? r.boChecked : !r.boChecked,
      ),
    [allItems, boFilter],
  );

  const goMonth = (delta: number) => {
    let y = year;
    let m = month + delta;
    if (m < 1) {
      m = 12;
      y -= 1;
    } else if (m > 12) {
      m = 1;
      y += 1;
    }
    setYM({ year: y, month: m });
  };

  const toggleRow = (id: string) => {
    setSelected((cur) => {
      const next = new Set(cur);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const allVisibleSelected = items.length > 0 && items.every((r) => selected.has(r.ticketId));
  const someSelected = selected.size > 0;

  const toggleAll = () => {
    setSelected((cur) => {
      if (items.every((r) => cur.has(r.ticketId)) && items.length > 0) {
        // Clear only the currently-visible rows from the selection.
        const next = new Set(cur);
        for (const r of items) next.delete(r.ticketId);
        return next;
      }
      const next = new Set(cur);
      for (const r of items) next.add(r.ticketId);
      return next;
    });
  };

  const bulkSet = (checked: boolean) => {
    const ids = [...selected];
    if (ids.length === 0) return;
    setChecks.mutate(
      { ids, checked },
      { onSuccess: () => setSelected(new Set()) },
    );
  };

  const statusesConfigured = list.data?.statusesConfigured ?? true;

  return (
    <TooltipProvider delayDuration={150}>
      <div className="flex flex-1 flex-col gap-4">
        {/* Toolbar */}
        <div className="glass-panel flex flex-wrap items-center justify-between gap-3 p-3">
          <div className="flex items-center gap-1">
            <Button
              size="sm"
              variant="ghost"
              onClick={() => goMonth(-1)}
              className="h-8 w-8 p-0"
              aria-label="Previous month"
            >
              <ChevronLeft className="h-4 w-4" />
            </Button>
            <MonthLabel year={year} month={month} />
            <Button
              size="sm"
              variant="ghost"
              onClick={() => goMonth(1)}
              className="h-8 w-8 p-0"
              aria-label="Next month"
            >
              <ChevronRight className="h-4 w-4" />
            </Button>
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                const d = new Date();
                setYM({ year: d.getFullYear(), month: d.getMonth() + 1 });
              }}
              className="h-8 px-2 text-xs"
            >
              This month
            </Button>
          </div>

          <div className="flex items-center gap-2">
            <BoFilterSelect value={boFilter} onChange={setBoFilter} />
            <ColumnPicker config={effectiveConfig} onChange={persistConfig} />
          </div>
        </div>

        {/* Bulk action bar */}
        {someSelected && (
          <div className="glass-panel flex items-center justify-between gap-3 px-3 py-2">
            <span className="text-xs text-muted-foreground">
              {selected.size} selected
            </span>
            <div className="flex items-center gap-2">
              <Button
                size="sm"
                variant="ghost"
                className="h-8 gap-1.5"
                disabled={setChecks.isPending}
                onClick={() => bulkSet(true)}
              >
                <CheckCheck className="h-3.5 w-3.5" />
                Mark BO checked
              </Button>
              <Button
                size="sm"
                variant="ghost"
                className="h-8 px-2 text-xs text-muted-foreground"
                disabled={setChecks.isPending}
                onClick={() => bulkSet(false)}
              >
                Unmark
              </Button>
              <Button
                size="sm"
                variant="ghost"
                className="h-8 px-2 text-xs text-muted-foreground"
                onClick={() => setSelected(new Set())}
              >
                Clear
              </Button>
            </div>
          </div>
        )}

        <div className="glass-panel flex-1 overflow-hidden">
          {list.isLoading ? (
            <div className="space-y-2 p-4">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
            </div>
          ) : list.isError ? (
            <div className="p-6 text-sm text-rose-300">
              Could not load tickets. Refresh and try again.
            </div>
          ) : !statusesConfigured ? (
            <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
              <Settings2 className="h-6 w-6 text-muted-foreground" />
              <p className="max-w-md text-sm text-muted-foreground">
                No statuses are configured for this tab yet. An admin selects
                them under <span className="text-foreground/80">Settings → Timesheet → Back-office tabs</span>.
              </p>
            </div>
          ) : items.length === 0 ? (
            <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
              <Inbox className="h-6 w-6 text-muted-foreground" />
              <p className="text-sm text-muted-foreground">
                {boFilter !== "all"
                  ? "No tickets match this filter for the selected month."
                  : "No tickets for the selected month."}
              </p>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                    <th className="w-10 px-3 py-2.5">
                      <input
                        type="checkbox"
                        checked={allVisibleSelected}
                        onChange={toggleAll}
                        aria-label="Select all"
                        className="h-3.5 w-3.5 rounded border border-glass-strong bg-glass accent-primary"
                      />
                    </th>
                    {visibleColumns.map((col) => (
                      <th
                        key={col.id}
                        className={cn(
                          "px-3 py-2.5 font-medium",
                          col.align === "right" && "text-right",
                        )}
                      >
                        {col.label}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {items.map((r) => (
                    <TicketRow
                      key={r.ticketId}
                      row={r}
                      columns={visibleColumns}
                      selected={selected.has(r.ticketId)}
                      onToggleSelect={() => toggleRow(r.ticketId)}
                      onSetCheck={(checked) =>
                        setChecks.mutate({ ids: [r.ticketId], checked })
                      }
                      busy={setChecks.isPending}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>

        {!list.isLoading && !list.isError && statusesConfigured && items.length > 0 && (
          <div className="text-xs text-muted-foreground">
            {items.length.toLocaleString()} ticket{items.length === 1 ? "" : "s"}
            {boFilter !== "all" && ` (${boFilter})`}
          </div>
        )}
      </div>
    </TooltipProvider>
  );
}

// ---- row --------------------------------------------------------------

function TicketRow({
  row,
  columns,
  selected,
  onToggleSelect,
  onSetCheck,
  busy,
}: {
  row: BackofficeTicket;
  columns: ColMeta[];
  selected: boolean;
  onToggleSelect: () => void;
  onSetCheck: (checked: boolean) => void;
  busy: boolean;
}) {
  return (
    <tr
      className={cn(
        "border-b border-glass transition-colors hover:bg-glass-hover",
        selected && "bg-glass-hover",
      )}
    >
      <td className="px-3 py-2.5">
        <input
          type="checkbox"
          checked={selected}
          onChange={onToggleSelect}
          aria-label={`Select ticket ${row.ticketNumber}`}
          className="h-3.5 w-3.5 rounded border border-glass-strong bg-glass accent-primary"
        />
      </td>
      {columns.map((col) => (
        <td
          key={col.id}
          className={cn("px-3 py-2.5", col.align === "right" && "text-right")}
        >
          {renderCell(col.id, row, onSetCheck, busy)}
        </td>
      ))}
    </tr>
  );
}

function renderCell(
  id: ColId,
  row: BackofficeTicket,
  onSetCheck: (checked: boolean) => void,
  busy: boolean,
) {
  switch (id) {
    case "number":
      return <span className="font-mono text-xs text-muted-foreground">#{row.ticketNumber}</span>;
    case "subject":
      return <span className="block max-w-[28rem] truncate text-foreground">{row.subject}</span>;
    case "customer":
      return <span className="text-muted-foreground">{row.companyName ?? "—"}</span>;
    case "hours":
      return <HoursCell row={row} />;
    case "bochecked":
      return <BoCheckedCell row={row} onSetCheck={onSetCheck} busy={busy} />;
    default:
      return null;
  }
}

// ---- hours pill -------------------------------------------------------

function HoursCell({ row }: { row: BackofficeTicket }) {
  const [open, setOpen] = useState(false);
  const hasHours = row.totalMinutes > 0;

  const breakdown = useQuery({
    queryKey: ["timesheet", "backoffice", "hours", row.ticketId],
    queryFn: () => backofficeTimesheetApi.ticketHours(row.ticketId),
    enabled: open && hasHours,
  });

  if (!hasHours) return <span className="text-muted-foreground/40">—</span>;

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="inline-flex items-center rounded-full border border-purple-400/30 bg-purple-500/[0.12] px-2.5 py-0.5 text-xs tabular-nums text-purple-200 transition-colors hover:bg-purple-500/20"
          title="Show hours per task"
        >
          {formatMinutes(row.totalMinutes)}
        </button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-64 p-0">
        <div className="border-b border-glass px-3 py-2">
          <div className="text-xs font-medium text-foreground">Hours per task</div>
          <div className="text-[11px] text-muted-foreground/70">Ticket #{row.ticketNumber}</div>
        </div>
        <div className="max-h-64 overflow-auto p-2">
          {breakdown.isLoading ? (
            <Skeleton className="h-12 w-full" />
          ) : breakdown.isError || !breakdown.data ? (
            <p className="px-1 py-2 text-xs text-rose-300">Could not load breakdown.</p>
          ) : breakdown.data.byTask.length === 0 ? (
            <p className="px-1 py-2 text-xs text-muted-foreground">No registered hours.</p>
          ) : (
            <ul className="space-y-0.5 text-xs">
              {breakdown.data.byTask.map((t) => (
                <li
                  key={t.taskName}
                  className="flex items-center justify-between gap-3 rounded px-1.5 py-1 hover:bg-glass-hover"
                >
                  <span className={cn("truncate", t.isAbsence ? "text-muted-foreground" : "text-foreground")}>
                    {t.taskName}
                    {t.isAbsence && <span className="ml-1 text-[10px] text-muted-foreground/60">(afwezig)</span>}
                  </span>
                  <span className="shrink-0 tabular-nums text-muted-foreground">{formatMinutes(t.minutes)}</span>
                </li>
              ))}
              <li className="mt-1 flex items-center justify-between gap-3 border-t border-glass px-1.5 pt-1.5 font-medium">
                <span className="text-foreground">Total</span>
                <span className="tabular-nums text-foreground">{formatMinutes(breakdown.data.totalMinutes)}</span>
              </li>
            </ul>
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}

// ---- BO-checked cell --------------------------------------------------

function BoCheckedCell({
  row,
  onSetCheck,
  busy,
}: {
  row: BackofficeTicket;
  onSetCheck: (checked: boolean) => void;
  busy: boolean;
}) {
  const checkbox = (
    <input
      type="checkbox"
      checked={row.boChecked}
      disabled={busy}
      onChange={(e) => onSetCheck(e.target.checked)}
      aria-label={row.boChecked ? "Uncheck BO checked" : "Mark BO checked"}
      className="h-4 w-4 rounded border border-glass-strong bg-glass accent-emerald-400 disabled:opacity-50"
    />
  );

  if (!row.boChecked) {
    return <div className="flex justify-end">{checkbox}</div>;
  }

  return (
    <div className="flex justify-end">
      <Tooltip>
        <TooltipTrigger asChild>
          <span className="inline-flex">{checkbox}</span>
        </TooltipTrigger>
        <TooltipContent>
          <div className="text-left">
            <div>Checked by {row.checkedByEmail ?? "unknown"}</div>
            <div className="text-primary-foreground/70">{formatDateTime(row.checkedUtc)}</div>
          </div>
        </TooltipContent>
      </Tooltip>
    </div>
  );
}

// ---- BO filter --------------------------------------------------------

function BoFilterSelect({
  value,
  onChange,
}: {
  value: BoFilter;
  onChange: (v: BoFilter) => void;
}) {
  return (
    <Select value={value} onValueChange={(v) => onChange(v as BoFilter)}>
      <SelectTrigger className="h-8 w-40 text-xs">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value="all">All</SelectItem>
        <SelectItem value="checked">BO checked</SelectItem>
        <SelectItem value="unchecked">Not checked</SelectItem>
      </SelectContent>
    </Select>
  );
}

// ---- column picker (toggle + drag-reorder) ----------------------------

function ColumnPicker({
  config,
  onChange,
}: {
  config: ColConfig[];
  onChange: (next: ColConfig[]) => void;
}) {
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }));

  const onDragEnd = (e: DragEndEvent) => {
    const { active, over } = e;
    if (!over || active.id === over.id) return;
    const from = config.findIndex((c) => c.id === active.id);
    const to = config.findIndex((c) => c.id === over.id);
    if (from < 0 || to < 0) return;
    onChange(arrayMove(config, from, to));
  };

  const toggle = (id: ColId) => {
    const visibleCount = config.filter((c) => c.visible).length;
    const target = config.find((c) => c.id === id);
    if (target?.visible && visibleCount <= 1) return; // keep ≥1 visible
    onChange(config.map((c) => (c.id === id ? { ...c, visible: !c.visible } : c)));
  };

  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button size="sm" variant="ghost" className="gap-1.5">
          <Columns3 className="h-4 w-4" />
          Columns
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-64 p-2">
        <div className="px-1 pb-2 text-[11px] uppercase tracking-wider text-muted-foreground/70">
          Columns — drag to reorder
        </div>
        <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
          <SortableContext items={config.map((c) => c.id)} strategy={verticalListSortingStrategy}>
            <ul className="space-y-0.5">
              {config.map((c) => (
                <ColumnPickerItem key={c.id} id={c.id} visible={c.visible} onToggle={() => toggle(c.id)} />
              ))}
            </ul>
          </SortableContext>
        </DndContext>
      </PopoverContent>
    </Popover>
  );
}

function ColumnPickerItem({
  id,
  visible,
  onToggle,
}: {
  id: ColId;
  visible: boolean;
  onToggle: () => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id });
  const style = { transform: CSS.Transform.toString(transform), transition };

  return (
    <li
      ref={setNodeRef}
      style={style}
      className={cn(
        "flex items-center gap-2 rounded-md px-1.5 py-1 text-sm",
        isDragging ? "bg-glass-strong" : "hover:bg-glass-hover",
      )}
    >
      <button
        type="button"
        className="cursor-grab touch-none text-muted-foreground/50 hover:text-muted-foreground active:cursor-grabbing"
        aria-label="Drag to reorder"
        {...attributes}
        {...listeners}
      >
        <GripVertical className="h-3.5 w-3.5" />
      </button>
      <label className="flex flex-1 cursor-pointer items-center gap-2">
        <input
          type="checkbox"
          checked={visible}
          onChange={onToggle}
          className="h-3.5 w-3.5 rounded border border-glass-strong bg-glass accent-purple-400"
        />
        <span className={cn(visible ? "text-foreground" : "text-muted-foreground")}>{COL_LABELS[id]}</span>
      </label>
    </li>
  );
}

// ---- month label ------------------------------------------------------

function MonthLabel({ year, month }: { year: number; month: number }) {
  const d = new Date(year, month - 1, 1);
  const label = d.toLocaleDateString(undefined, { month: "long", year: "numeric" });
  return (
    <span className="min-w-[10rem] px-2 text-center text-sm font-medium text-foreground">
      {label}
    </span>
  );
}
