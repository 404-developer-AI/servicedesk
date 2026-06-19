import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowDown,
  ArrowUp,
  CheckCheck,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Columns3,
  CornerDownRight,
  ExternalLink,
  GripVertical,
  Receipt,
  RefreshCw,
  Search,
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
  ApiError,
  adsolutTimesheetApi,
  backofficeTimesheetApi,
  preferencesApi,
  type AdsolutSalesReceiptDetail,
  type AdsolutSalesReceiptHeader,
} from "@/lib/api";
import { openTicketInSharedWindow } from "@/lib/ticketWindow";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
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

const PAGE_SIZE = 50;
const RECEIPTS_KEY = ["timesheet", "adsolut", "receipts"] as const;
// Per-agent column config (visibility + order) — persisted server-side via the
// generic workspace-preferences store, so it follows the agent across devices.
const COLUMNS_PREF_KEY = "workspace:adsolut-receipts-columns";

type SortDir = "asc" | "desc";
type BoFilter = "all" | "checked" | "unchecked";

// ---- formatting helpers ----------------------------------------------

function formatDate(iso: string | null | undefined) {
  if (!iso) return "—";
  try {
    return new Intl.DateTimeFormat(undefined, { year: "numeric", month: "short", day: "2-digit" }).format(new Date(iso));
  } catch {
    return iso;
  }
}

function formatDateTime(iso: string | null | undefined) {
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

function formatMoney(value: number | null | undefined, currency: string | null | undefined) {
  if (value === null || value === undefined) return "—";
  try {
    return new Intl.NumberFormat("nl-BE", { style: "currency", currency: currency || "EUR" }).format(value);
  } catch {
    return value.toFixed(2);
  }
}

/// Minutes → "3u 30m" (or "45m" / "2u"). Empty marker for null/0.
function formatMinutes(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined || minutes <= 0) return "—";
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}u`;
  return `${h}u ${m}m`;
}

/// Split a product line's name + description into a bold label and the free-text
/// detail a technician added on the verkoopbon. Adsolut repeats the article
/// label as the first line of `description` and writes the extra notes (bullets)
/// underneath, so: label = name (or the description's first line when the name
/// is empty), detail = every line after the first. CRs are stripped; the detail
/// keeps its line breaks/indentation for whitespace-pre-line rendering.
function splitLineText(
  name: string | null | undefined,
  description: string | null | undefined,
): { label: string; detail: string } {
  const desc = (description ?? "").replace(/\r/g, "");
  const lines = desc.length > 0 ? desc.split("\n") : [];
  const firstLine = lines.length > 0 ? lines[0].trim() : "";
  const label = (name ?? "").trim() || firstLine || "—";
  const detail = lines.slice(1).join("\n").trim();
  return { label, detail };
}

// ---- column model -----------------------------------------------------

type ColId =
  | "doc"
  | "ticket"
  | "date"
  | "customer"
  | "status"
  | "description"
  | "employee"
  | "total"
  | "werkuren"
  | "hours"
  | "bruto"
  | "difference"
  | "bochecked";

type ColDef = {
  id: ColId;
  label: string;
  align: "left" | "right";
  /** Server sort key — present = the header is clickable to sort. */
  sortKey?: string;
  /** Cell holds interactive content (a pill) → don't toggle row-expand on click. */
  interactive?: boolean;
  render: (r: AdsolutSalesReceiptHeader) => React.ReactNode;
};

const ALL_COLUMNS: ColDef[] = [
  {
    id: "doc",
    label: "Doc",
    align: "left",
    sortKey: "doc",
    render: (r) => (
      <span className="font-mono text-xs text-muted-foreground">
        {r.bookCode ? `${r.bookCode} ` : ""}
        {r.docNr ?? "—"}
      </span>
    ),
  },
  {
    id: "ticket",
    label: "Ticket",
    align: "left",
    interactive: true,
    render: (r) => <TicketNumberLink ticketNumber={r.ticketNumber} ticketId={r.ticketId} />,
  },
  {
    id: "date",
    label: "Date",
    align: "left",
    sortKey: "date",
    render: (r) => <span className="whitespace-nowrap">{formatDate(r.salesReceiptDate)}</span>,
  },
  {
    id: "customer",
    label: "Customer",
    align: "left",
    sortKey: "customer",
    render: (r) => (
      <>
        <span className="text-foreground">{r.customerName ?? "—"}</span>
        {r.customerCode && <span className="ml-1.5 text-[11px] text-muted-foreground/60">{r.customerCode}</span>}
      </>
    ),
  },
  {
    id: "status",
    label: "Status",
    align: "left",
    sortKey: "status",
    render: (r) =>
      r.stateCode ? (
        <span className="inline-flex items-center rounded-full border border-glass-strong bg-glass px-2 py-0.5 text-[11px] text-muted-foreground">
          {r.stateDescription ?? r.stateCode}
        </span>
      ) : (
        "—"
      ),
  },
  {
    id: "description",
    label: "Description",
    align: "left",
    render: (r) => <span className="block max-w-[20rem] truncate text-muted-foreground">{r.description || "—"}</span>,
  },
  {
    id: "employee",
    label: "Employee",
    align: "left",
    render: (r) => (
      <span className="whitespace-nowrap text-muted-foreground">{r.employeeName ?? r.employeeCode ?? "—"}</span>
    ),
  },
  {
    id: "total",
    label: "Total (excl. VAT)",
    align: "right",
    sortKey: "total",
    render: (r) => (
      <span className="whitespace-nowrap tabular-nums text-foreground">{formatMoney(r.totalExclVat, r.currencyIso)}</span>
    ),
  },
  {
    id: "werkuren",
    label: "VK Werkuren",
    align: "right",
    sortKey: "werkuren",
    render: (r) => <WerkurenCell receipt={r} />,
  },
  {
    id: "hours",
    label: "Hours",
    align: "right",
    sortKey: "hours",
    interactive: true,
    render: (r) => <HoursCell receipt={r} />,
  },
  {
    id: "bruto",
    label: "Bruto Price",
    align: "right",
    sortKey: "bruto",
    interactive: true,
    render: (r) => <BrutoPriceCell receipt={r} />,
  },
  {
    id: "difference",
    label: "Difference",
    align: "right",
    sortKey: "difference",
    render: (r) => <DifferenceCell receipt={r} />,
  },
  {
    id: "bochecked",
    label: "BO checked",
    align: "right",
    interactive: true,
    render: (r) => <BoCheckedCell receipt={r} />,
  },
];

const COL_LABELS: Record<ColId, string> = Object.fromEntries(
  ALL_COLUMNS.map((c) => [c.id, c.label]),
) as Record<ColId, string>;

type ColConfig = { id: ColId; visible: boolean };

const DEFAULT_CONFIG: ColConfig[] = ALL_COLUMNS.map((c) => ({ id: c.id, visible: true }));

/// Merge a saved config with ALL_COLUMNS: keep known ids in their saved order,
/// drop unknown ids, and append any columns added since the config was saved
/// (visible by default) so a new column never silently disappears.
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

// ---- month switcher label ---------------------------------------------

function MonthLabel({ year, month, all }: { year: number; month: number; all: boolean }) {
  const d = new Date(year, month - 1, 1);
  const label = all
    ? "All months"
    : d.toLocaleDateString(undefined, { month: "long", year: "numeric" });
  return (
    <span className="min-w-[9rem] px-2 text-center text-sm font-medium text-foreground">
      {label}
    </span>
  );
}

// ---- main tab ---------------------------------------------------------

/// Timesheet → Adsolut tab. Mounted only when the Adsolut integration is
/// connected AND the user has the "Adsolut Timesheet" feature flag (enforced
/// in TimesheetPage). Lists the mirrored sales receipts with expandable
/// product + performance lines, registered-hours + bruto-price + difference
/// columns, per-agent column visibility/order, server-side sorting, and a
/// per-row resync.
export function TimesheetTabAdsolut() {
  const qc = useQueryClient();
  const [search, setSearch] = useState("");
  // Single-month scope on the receipt date, mirroring the Resolved/CWI tabs:
  // prev/next stepping and a "This month" reset. Defaults to the current month.
  // "All" lifts the month scope entirely (every mirrored receipt) — stepping a
  // month or "This month" re-enables scoping from the last selected month.
  const [{ year, month }, setYM] = useState(() => {
    const d = new Date();
    return { year: d.getFullYear(), month: d.getMonth() + 1 };
  });
  const [allMonths, setAllMonths] = useState(false);
  const goMonth = (delta: number) => {
    setPage(1);
    setAllMonths(false);
    setYM(({ year: yy, month: mm }) => {
      let y = yy;
      let m = mm + delta;
      if (m < 1) {
        m = 12;
        y -= 1;
      } else if (m > 12) {
        m = 1;
        y += 1;
      }
      return { year: y, month: m };
    });
  };
  const thisMonth = () => {
    const d = new Date();
    setPage(1);
    setAllMonths(false);
    setYM({ year: d.getFullYear(), month: d.getMonth() + 1 });
  };
  const showAll = () => {
    setPage(1);
    setAllMonths(true);
  };
  const [page, setPage] = useState(1);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [sortKey, setSortKey] = useState("date");
  const [sortDir, setSortDir] = useState<SortDir>("desc");
  const [boFilter, setBoFilter] = useState<BoFilter>("all");
  const [selected, setSelected] = useState<Set<string>>(new Set());

  // Per-agent column config — load once from the workspace prefs, then own it
  // locally and persist on every change.
  const wsQuery = useQuery({
    queryKey: ["preferences", "workspace"],
    queryFn: () => preferencesApi.getWorkspace(),
    staleTime: 60_000,
  });
  const [config, setConfig] = useState<ColConfig[] | null>(null);
  useEffect(() => {
    if (config !== null || wsQuery.data === undefined) return;
    setConfig(mergeConfig(wsQuery.data[COLUMNS_PREF_KEY]));
  }, [wsQuery.data, config]);
  const effectiveConfig = config ?? DEFAULT_CONFIG;

  const persistConfig = (next: ColConfig[]) => {
    setConfig(next);
    preferencesApi.saveWorkspace([{ key: COLUMNS_PREF_KEY, value: JSON.stringify(next) }]).catch(() => {
      /* best-effort */
    });
  };

  const visibleColumns = useMemo(
    () =>
      effectiveConfig
        .filter((c) => c.visible)
        .map((c) => ALL_COLUMNS.find((d) => d.id === c.id))
        .filter((d): d is ColDef => d !== undefined),
    [effectiveConfig],
  );

  const scopeYear = allMonths ? undefined : year;
  const scopeMonth = allMonths ? undefined : month;
  const list = useQuery({
    queryKey: [...RECEIPTS_KEY, search, page, sortKey, sortDir, boFilter, scopeYear, scopeMonth],
    queryFn: () =>
      adsolutTimesheetApi.listReceipts(search, page, PAGE_SIZE, sortKey, sortDir, boFilter, scopeYear, scopeMonth),
  });

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const colCount = visibleColumns.length + 3; // select + chevron + columns + resync
  const pageItems = list.data?.items ?? [];

  // Clear the selection whenever the visible rows change (paging, search,
  // sort or BO filter), so a hidden row never lingers in a bulk action.
  useEffect(() => {
    setSelected(new Set());
  }, [page, search, sortKey, sortDir, boFilter, scopeYear, scopeMonth]);

  const setChecks = useMutation({
    mutationFn: (vars: { ids: string[]; checked: boolean }) =>
      backofficeTimesheetApi.setChecks("adsolut", vars.ids, vars.checked),
    onSuccess: (_res, vars) => {
      qc.invalidateQueries({ queryKey: RECEIPTS_KEY });
      toast.success(
        vars.checked ? `${vars.ids.length} marked BO checked` : `${vars.ids.length} unmarked`,
      );
    },
    onError: () => toast.error("Could not update BO check"),
  });

  const toggleRow = (id: string) =>
    setSelected((cur) => {
      const next = new Set(cur);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const allPageSelected = pageItems.length > 0 && pageItems.every((r) => selected.has(r.id));

  const toggleAll = () =>
    setSelected((cur) => {
      if (pageItems.length > 0 && pageItems.every((r) => cur.has(r.id))) return new Set();
      return new Set(pageItems.map((r) => r.id));
    });

  const bulkSet = (checked: boolean) => {
    const ids = [...selected];
    if (ids.length === 0) return;
    setChecks.mutate({ ids, checked }, { onSuccess: () => setSelected(new Set()) });
  };

  const onSort = (key: string) => {
    if (sortKey === key) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(key);
      setSortDir("desc");
    }
    setPage(1);
  };

  return (
   <TooltipProvider delayDuration={150}>
    <div className="flex flex-1 flex-col gap-4">
      <div className="flex items-center gap-2">
        <div className="flex h-9 w-9 items-center justify-center rounded-full border border-glass bg-glass">
          <Receipt className="h-4 w-4 text-muted-foreground" />
        </div>
        <div>
          <h2 className="text-display-sm font-semibold text-foreground">Sales receipts</h2>
          <p className="text-xs text-muted-foreground">
            {total.toLocaleString()} verkoopbonnen mirrored from Adsolut
          </p>
        </div>
      </div>

      {/* Toolbar: month scope (left) + search / filters (right). */}
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
          <MonthLabel year={year} month={month} all={allMonths} />
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
            onClick={thisMonth}
            className="h-8 px-2 text-xs"
          >
            This month
          </Button>
          <Button
            size="sm"
            variant="ghost"
            onClick={showAll}
            aria-pressed={allMonths}
            className={cn(
              "h-8 px-2 text-xs",
              allMonths && "bg-primary/15 text-primary hover:bg-primary/20",
            )}
          >
            All
          </Button>
        </div>
        <div className="flex items-center gap-2">
          <div className="relative w-64 max-w-xs">
            <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setPage(1);
              }}
              placeholder="Search customer, description, doc nr…"
              className="pl-9"
            />
          </div>
          <BoFilterSelect
            value={boFilter}
            onChange={(v) => {
              setBoFilter(v);
              setPage(1);
            }}
          />
          <ColumnPicker config={effectiveConfig} onChange={persistConfig} />
        </div>
      </div>

      {selected.size > 0 && (
        <div className="glass-panel flex items-center justify-between gap-3 px-3 py-2">
          <span className="text-xs text-muted-foreground">{selected.size} selected</span>
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
            Could not load sales receipts. Refresh or check the integration status.
          </div>
        ) : (list.data?.items.length ?? 0) === 0 ? (
          <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
            <Receipt className="h-6 w-6 text-muted-foreground" />
            <p className="text-sm text-muted-foreground">
              {search.trim()
                ? "No receipts match your search."
                : "No receipts mirrored yet. An admin enables the pull under Settings → Integrations → Adsolut."}
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
                      checked={allPageSelected}
                      onChange={toggleAll}
                      aria-label="Select all on this page"
                      className="h-3.5 w-3.5 rounded border border-glass-strong bg-glass accent-primary"
                    />
                  </th>
                  <th className="w-8 px-3 py-2.5" />
                  {visibleColumns.map((col) => {
                    const active = col.sortKey && sortKey === col.sortKey;
                    return (
                      <th
                        key={col.id}
                        className={cn(
                          "px-3 py-2.5 font-medium",
                          col.align === "right" && "text-right",
                          col.sortKey && "cursor-pointer select-none hover:text-foreground",
                        )}
                        onClick={col.sortKey ? () => onSort(col.sortKey!) : undefined}
                      >
                        <span className={cn("inline-flex items-center gap-1", col.align === "right" && "flex-row-reverse")}>
                          {col.label}
                          {active &&
                            (sortDir === "asc" ? (
                              <ArrowUp className="h-3 w-3" />
                            ) : (
                              <ArrowDown className="h-3 w-3" />
                            ))}
                        </span>
                      </th>
                    );
                  })}
                  <th className="w-10 px-3 py-2.5" />
                </tr>
              </thead>
              <tbody>
                {list.data!.items.map((r) => (
                  <ReceiptRow
                    key={r.id}
                    receipt={r}
                    columns={visibleColumns}
                    colCount={colCount}
                    expanded={expandedId === r.id}
                    onToggle={() => setExpandedId((cur) => (cur === r.id ? null : r.id))}
                    selected={selected.has(r.id)}
                    onToggleSelect={() => toggleRow(r.id)}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {total > PAGE_SIZE && (
        <div className="flex items-center justify-between text-xs text-muted-foreground">
          <span>
            Page {page} of {totalPages} · {total.toLocaleString()} total
          </span>
          <div className="flex items-center gap-2">
            <Button
              size="sm"
              variant="ghost"
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={page <= 1 || list.isFetching}
            >
              Previous
            </Button>
            <Button
              size="sm"
              variant="ghost"
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              disabled={page >= totalPages || list.isFetching}
            >
              Next
            </Button>
          </div>
        </div>
      )}
    </div>
   </TooltipProvider>
  );
}

// ---- row --------------------------------------------------------------

function ReceiptRow({
  receipt,
  columns,
  colCount,
  expanded,
  onToggle,
  selected,
  onToggleSelect,
}: {
  receipt: AdsolutSalesReceiptHeader;
  columns: ColDef[];
  colCount: number;
  expanded: boolean;
  onToggle: () => void;
  selected: boolean;
  onToggleSelect: () => void;
}) {
  const qc = useQueryClient();
  const detail = useQuery({
    queryKey: ["timesheet", "adsolut", "receipt", receipt.id],
    queryFn: () => adsolutTimesheetApi.receiptDetail(receipt.id),
    enabled: expanded,
  });

  const resync = useMutation({
    mutationFn: () => adsolutTimesheetApi.resyncReceipt(receipt.id),
    onSuccess: () => {
      toast.success(`Receipt ${receipt.docNr ?? ""} resynced`);
      qc.invalidateQueries({ queryKey: ["timesheet", "adsolut", "receipt", receipt.id] });
      qc.invalidateQueries({ queryKey: ["timesheet", "adsolut", "hours", receipt.id] });
      qc.invalidateQueries({ queryKey: RECEIPTS_KEY });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Resync failed (${err.status})` : "Resync failed"),
  });

  return (
    <>
      <tr
        className={cn(
          "cursor-pointer border-b border-glass transition-colors",
          // Highlight the row you clicked open with the purple accent so it
          // clearly reads as "selected" — distinct from the neutral hover. The
          // expanded detail panel below keeps its own (un-highlighted) styling.
          expanded ? "bg-purple-500/[0.13] hover:bg-purple-500/[0.16]" : "hover:bg-glass-hover",
        )}
        onClick={onToggle}
      >
        <td className="px-3 py-2.5" onClick={(e) => e.stopPropagation()}>
          <input
            type="checkbox"
            checked={selected}
            onChange={onToggleSelect}
            aria-label={`Select receipt ${receipt.docNr ?? ""}`}
            className="h-3.5 w-3.5 rounded border border-glass-strong bg-glass accent-primary"
          />
        </td>
        <td className="px-3 py-2.5 text-muted-foreground">
          {expanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
        </td>
        {columns.map((col) => (
          <td
            key={col.id}
            className={cn("px-3 py-2.5", col.align === "right" && "text-right")}
            onClick={col.interactive ? (e) => e.stopPropagation() : undefined}
          >
            {col.render(receipt)}
          </td>
        ))}
        <td className="px-3 py-2.5 text-right">
          <Button
            size="sm"
            variant="ghost"
            className="h-7 w-7 p-0"
            aria-label="Resync this receipt"
            onClick={(e) => {
              e.stopPropagation();
              resync.mutate();
            }}
            disabled={resync.isPending}
          >
            <RefreshCw className={cn("h-3.5 w-3.5", resync.isPending && "animate-spin")} />
          </Button>
        </td>
      </tr>

      {expanded && (
        <tr className="border-b border-glass bg-purple-500/[0.05]">
          <td colSpan={colCount} className="px-6 py-4">
            {detail.isLoading ? (
              <Skeleton className="h-20 w-full" />
            ) : detail.isError || !detail.data ? (
              <p className="text-xs text-rose-300">Could not load receipt detail.</p>
            ) : (
              <ReceiptDetail data={detail.data} />
            )}
          </td>
        </tr>
      )}
    </>
  );
}

// ---- clickable ticket number ------------------------------------------

/// The receipt's ticket number, rendered as a single clickable control. One
/// click copies the *bare* number (no "Ticket#" prefix) to the clipboard and,
/// when the ticket exists in this install, opens it in the shared ticket
/// window (reused across clicks, brought to the front each time). When no
/// ticket matches (ticketId null) it still copies, but cannot open.
function TicketNumberLink({
  ticketNumber,
  ticketId,
}: {
  ticketNumber: number | null;
  ticketId: string | null;
}) {
  if (ticketNumber === null) return <span className="text-muted-foreground/40">—</span>;

  const handleClick = async () => {
    try {
      await navigator.clipboard.writeText(String(ticketNumber));
      toast.success(ticketId ? `Ticket #${ticketNumber} copied — opening…` : `Ticket #${ticketNumber} copied`);
    } catch {
      toast.error("Could not copy the ticket number");
    }
    if (ticketId) openTicketInSharedWindow(ticketId);
  };

  return (
    <button
      type="button"
      onClick={handleClick}
      title={ticketId ? "Copy number & open the ticket" : "Copy number (no matching ticket in this install)"}
      className="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 font-mono text-xs text-purple-200 transition-colors hover:bg-purple-500/15 hover:text-purple-100"
    >
      #{ticketNumber}
      {ticketId && <ExternalLink className="h-3 w-3 opacity-50" />}
    </button>
  );
}

// ---- multi-receipt grouping -------------------------------------------

/// True when this receipt is a *sibling* — a non-primary receipt of a ticket
/// that is billed across several verkoopbonnen. Its registered hours (and the
/// bruto / difference derived from them) live once on the primary receipt, so
/// the hours/bruto/difference cells here show a pointer instead of a figure.
function isSibling(receipt: AdsolutSalesReceiptHeader): boolean {
  return receipt.ticketReceiptCount > 1 && !receipt.isPrimary;
}

/// Muted pointer rendered in the hours/bruto/difference cells of a sibling
/// receipt. The numbers live on the ticket's primary receipt; here we only
/// point back to the ticket (ordinal/count) so nothing is double-counted.
function SiblingMarker({ receipt }: { receipt: AdsolutSalesReceiptHeader }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex items-center gap-1 text-xs text-muted-foreground/50">
          <CornerDownRight className="h-3 w-3 shrink-0" />
          <span className="whitespace-nowrap">
            {receipt.ticketNumber ? `#${receipt.ticketNumber}` : "ticket"}{" "}
            <span className="tabular-nums">
              ({receipt.ticketReceiptOrdinal}/{receipt.ticketReceiptCount})
            </span>
          </span>
        </span>
      </TooltipTrigger>
      <TooltipContent>
        Part of ticket #{receipt.ticketNumber} — the hours are shown once on the first receipt of
        this ticket, compared against the combined VK Werkuren of all {receipt.ticketReceiptCount}{" "}
        receipts.
      </TooltipContent>
    </Tooltip>
  );
}

// ---- VK Werkuren cell -------------------------------------------------

/// This receipt's "VK Werkuren": the excl-VAT total of only the product lines
/// whose product is flagged (Settings → Timesheet) as counting toward work
/// hours. Hardware and other lines are excluded. Shown muted when 0 (no
/// work-hours line, or the catalogue isn't synced/flagged yet) so it still
/// explains a negative Difference.
function WerkurenCell({ receipt }: { receipt: AdsolutSalesReceiptHeader }) {
  const value = receipt.werkurenExclVat;
  if (!value) {
    return (
      <span className="whitespace-nowrap tabular-nums text-muted-foreground/40">
        {formatMoney(0, receipt.currencyIso)}
      </span>
    );
  }
  return (
    <span className="whitespace-nowrap tabular-nums text-purple-200">
      {formatMoney(value, receipt.currencyIso)}
    </span>
  );
}

// ---- difference cell --------------------------------------------------

/// Combined VK Werkuren (excl. VAT) − Bruto Price. Green when positive
/// (margin), red when negative. Empty when there's no bruto price (no rate /
/// no hours). The comparison runs against the work-hours total (VK Werkuren),
/// not the full excl-VAT total, so hardware lines never skew it. For a ticket
/// billed across several verkoopbonnen the comparison runs once, on the primary
/// receipt, against the COMBINED VK Werkuren of every receipt on that ticket —
/// siblings show a pointer (SiblingMarker) instead.
function DifferenceCell({ receipt }: { receipt: AdsolutSalesReceiptHeader }) {
  if (isSibling(receipt)) return <SiblingMarker receipt={receipt} />;
  const billed = receipt.combinedWerkurenExclVat;
  if (receipt.brutoPrice === null || billed === null || billed === undefined) {
    return <span className="text-muted-foreground/40">—</span>;
  }
  const diff = billed - receipt.brutoPrice;
  const sign = diff > 0 ? "+ " : diff < 0 ? "− " : "";
  const tone = diff > 0 ? "text-emerald-300" : diff < 0 ? "text-rose-300" : "text-muted-foreground";
  return (
    <span className={cn("whitespace-nowrap tabular-nums", tone)}>
      {sign}
      {formatMoney(Math.abs(diff), receipt.currencyIso)}
    </span>
  );
}

// ---- hours pill -------------------------------------------------------

function HoursCell({ receipt }: { receipt: AdsolutSalesReceiptHeader }) {
  const [open, setOpen] = useState(false);
  const hasHours = receipt.totalMinutes !== null && receipt.totalMinutes > 0;

  const breakdown = useQuery({
    queryKey: ["timesheet", "adsolut", "hours", receipt.id],
    queryFn: () => adsolutTimesheetApi.receiptHours(receipt.id),
    enabled: open && hasHours,
  });

  if (isSibling(receipt)) return <SiblingMarker receipt={receipt} />;
  if (!hasHours) return <span className="text-muted-foreground/40">—</span>;

  // At this point a multi-receipt ticket means we're on its primary receipt:
  // the hours cover the whole ticket, so flag how many receipts they span.
  const grouped = receipt.ticketReceiptCount > 1;
  return (
    <div className="inline-flex items-center gap-1.5">
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="inline-flex items-center rounded-full border border-purple-400/30 bg-purple-500/[0.12] px-2.5 py-0.5 text-xs tabular-nums text-purple-200 transition-colors hover:bg-purple-500/20"
          title="Show hours per task"
        >
          {formatMinutes(receipt.totalMinutes)}
        </button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-64 p-0">
        <div className="border-b border-glass px-3 py-2">
          <div className="text-xs font-medium text-foreground">Hours per task</div>
          {receipt.ticketNumber && (
            <div className="text-[11px] text-muted-foreground/70">Ticket #{receipt.ticketNumber}</div>
          )}
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
    {grouped && (
      <Tooltip>
        <TooltipTrigger asChild>
          <span className="inline-flex items-center gap-0.5 rounded-full border border-amber-400/30 bg-amber-500/[0.12] px-1.5 py-0.5 text-[10px] tabular-nums text-amber-200">
            <Receipt className="h-2.5 w-2.5" />
            {receipt.ticketReceiptCount}
          </span>
        </TooltipTrigger>
        <TooltipContent>
          These hours cover all {receipt.ticketReceiptCount} receipts of ticket #
          {receipt.ticketNumber} — the Difference compares them against the combined VK Werkuren of
          all{" "}
          {receipt.ticketReceiptCount} receipts.
        </TooltipContent>
      </Tooltip>
    )}
    </div>
  );
}

// ---- bruto-price pill -------------------------------------------------

function BrutoPriceCell({ receipt }: { receipt: AdsolutSalesReceiptHeader }) {
  const [open, setOpen] = useState(false);
  const hasPrice = receipt.brutoPrice !== null && receipt.brutoPrice > 0;

  const breakdown = useQuery({
    queryKey: ["timesheet", "adsolut", "hours", receipt.id],
    queryFn: () => adsolutTimesheetApi.receiptHours(receipt.id),
    enabled: open && hasPrice,
  });

  if (isSibling(receipt)) return <SiblingMarker receipt={receipt} />;
  if (!hasPrice) return <span className="text-muted-foreground/40">—</span>;

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="inline-flex items-center rounded-full border border-emerald-400/30 bg-emerald-500/[0.12] px-2.5 py-0.5 text-xs tabular-nums text-emerald-200 transition-colors hover:bg-emerald-500/20"
          title="Show gross price per task"
        >
          {formatMoney(receipt.brutoPrice, receipt.currencyIso)}
        </button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-72 p-0">
        <div className="border-b border-glass px-3 py-2">
          <div className="text-xs font-medium text-foreground">Bruto Price per task</div>
          {receipt.ticketNumber && (
            <div className="text-[11px] text-muted-foreground/70">Ticket #{receipt.ticketNumber}</div>
          )}
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
                    <span className="ml-1 text-[10px] text-muted-foreground/60">{formatMinutes(t.minutes)}</span>
                  </span>
                  <span className="shrink-0 tabular-nums text-emerald-200">
                    {formatMoney(t.brutoPrice, receipt.currencyIso)}
                  </span>
                </li>
              ))}
              <li className="mt-1 flex items-center justify-between gap-3 border-t border-glass px-1.5 pt-1.5 font-medium">
                <span className="text-foreground">Total</span>
                <span className="tabular-nums text-foreground">
                  {formatMoney(breakdown.data.totalBrutoPrice, receipt.currencyIso)}
                </span>
              </li>
            </ul>
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}

// ---- BO-checked cell --------------------------------------------------

function BoCheckedCell({ receipt }: { receipt: AdsolutSalesReceiptHeader }) {
  const qc = useQueryClient();
  const setCheck = useMutation({
    mutationFn: (checked: boolean) => backofficeTimesheetApi.setChecks("adsolut", [receipt.id], checked),
    onSuccess: () => qc.invalidateQueries({ queryKey: RECEIPTS_KEY }),
    onError: () => toast.error("Could not update BO check"),
  });

  const checkbox = (
    <input
      type="checkbox"
      checked={receipt.boChecked}
      disabled={setCheck.isPending}
      onChange={(e) => setCheck.mutate(e.target.checked)}
      aria-label={receipt.boChecked ? "Uncheck BO checked" : "Mark BO checked"}
      className="h-4 w-4 rounded border border-glass-strong bg-glass accent-emerald-400 disabled:opacity-50"
    />
  );

  if (!receipt.boChecked) return <div className="flex justify-end">{checkbox}</div>;

  return (
    <div className="flex justify-end">
      <Tooltip>
        <TooltipTrigger asChild>
          <span className="inline-flex">{checkbox}</span>
        </TooltipTrigger>
        <TooltipContent>
          <div className="text-left">
            <div>Checked by {receipt.checkedByEmail ?? "unknown"}</div>
            <div className="text-primary-foreground/70">{formatDateTime(receipt.checkedUtc)}</div>
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
    // Keep at least one column visible.
    if (target?.visible && visibleCount <= 1) return;
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

// ---- expand detail ----------------------------------------------------

function ReceiptDetail({ data }: { data: AdsolutSalesReceiptDetail }) {
  const { header, lines, performances } = data;
  const currency = header.currencyIso;
  return (
    <div className="space-y-4">
      <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs sm:grid-cols-4">
        <div>
          <dt className="text-muted-foreground/60">Representative</dt>
          <dd className="text-foreground">{header.representativeName ?? "—"}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Receipt date</dt>
          <dd className="text-foreground">{formatDate(header.salesReceiptDate)}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Created (Adsolut)</dt>
          <dd className="text-foreground">{formatDateTime(header.adsolutCreatedUtc)}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Last synced</dt>
          <dd className="text-foreground">{formatDateTime(header.syncedUtc)}</dd>
        </div>
      </dl>

      {lines.length > 0 && (
        <div>
          <h4 className="mb-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground/70">
            Product lines
          </h4>
          <table className="w-full text-xs">
            <thead>
              <tr className="text-left text-muted-foreground/60">
                <th className="py-1 pr-3 font-medium">#</th>
                <th className="py-1 pr-3 font-medium">Product</th>
                <th className="py-1 pr-3 font-medium">Name</th>
                <th className="py-1 pr-3 text-right font-medium">Qty</th>
                <th className="py-1 pr-3 text-right font-medium">Unit price</th>
                <th className="py-1 text-right font-medium">Total (excl.)</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l) => {
                const { label, detail } = splitLineText(l.name, l.description);
                return (
                  <tr key={l.id} className="border-t border-glass align-top">
                    <td className="py-1 pr-3 text-muted-foreground">{l.lineNr ?? "—"}</td>
                    <td className="py-1 pr-3 font-mono text-muted-foreground">{l.productCode ?? "—"}</td>
                    <td className="py-1 pr-3">
                      <span className="text-foreground">{label}</span>
                      {detail && (
                        <p className="mt-0.5 whitespace-pre-line text-[11px] leading-snug text-muted-foreground/80">
                          {detail}
                        </p>
                      )}
                    </td>
                    <td className="py-1 pr-3 text-right tabular-nums">{l.quantity ?? "—"}</td>
                    <td className="py-1 pr-3 text-right tabular-nums">{formatMoney(l.unitPrice, currency)}</td>
                    <td className="py-1 text-right tabular-nums text-foreground">
                      {formatMoney(l.totalExclVat, currency)}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {performances.length > 0 && (
        <div>
          <h4 className="mb-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground/70">
            Performance lines (labour)
          </h4>
          <table className="w-full text-xs">
            <thead>
              <tr className="text-left text-muted-foreground/60">
                <th className="py-1 pr-3 font-medium">Employee</th>
                <th className="py-1 pr-3 font-medium">Date</th>
                <th className="py-1 pr-3 font-medium">From–Until</th>
                <th className="py-1 pr-3 text-right font-medium">Minutes</th>
                <th className="py-1 pr-3 font-medium">Description</th>
                <th className="py-1 text-right font-medium">Invoiced (excl.)</th>
              </tr>
            </thead>
            <tbody>
              {performances.map((p) => (
                <tr key={p.id} className="border-t border-glass">
                  <td className="py-1 pr-3 font-mono text-muted-foreground">{p.employeeCode ?? "—"}</td>
                  <td className="whitespace-nowrap py-1 pr-3">{formatDate(p.performanceDate)}</td>
                  <td className="whitespace-nowrap py-1 pr-3 text-muted-foreground">
                    {p.fromTime ?? "—"}–{p.untilTime ?? "—"}
                  </td>
                  <td className="py-1 pr-3 text-right tabular-nums">{p.invoiceDurationMinutes ?? p.durationMinutes ?? "—"}</td>
                  <td className="max-w-[24rem] truncate py-1 pr-3 text-muted-foreground">{p.description || "—"}</td>
                  <td className="py-1 text-right tabular-nums text-foreground">
                    {formatMoney(p.invoiceTotal, currency)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {lines.length === 0 && performances.length === 0 && (
        <p className="text-xs text-muted-foreground">This receipt has no product or performance lines.</p>
      )}
    </div>
  );
}
