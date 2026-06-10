import { createContext, useContext, useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowDown,
  ArrowUp,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Columns3,
  FileSignature,
  GripVertical,
  RefreshCw,
  Search,
} from "lucide-react";
import { Link } from "@tanstack/react-router";
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
  contractsApi,
  preferencesApi,
  type AdsolutContractHeader,
} from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import { ContractDetail } from "./ContractDetail";

const PAGE_SIZE = 50;
const CONTRACTS_KEY = ["contracts", "overview", "list"] as const;
const COLUMNS_PREF_KEY = "workspace:contracts-overview-columns";

type SortDir = "asc" | "desc";

// Admin-configured status code → hex colour map, provided by the page from the
// list response so the status column's render (which only receives the row) can
// colour its pill. Empty map = every status renders as a neutral glass pill.
const StatusColorsContext = createContext<Record<string, string>>({});

/// Status pill — takes the admin's colour for the contract's state code; a
/// status with no configured colour (or a contract with no state) falls back to
/// a neutral glass pill. Tint/border/text derive from the hex, matching the
/// order-detail "Bestelling status" chips.
function ContractStatusPill({ row }: { row: AdsolutContractHeader }) {
  const colors = useContext(StatusColorsContext);
  if (!row.stateCode) return <span className="text-muted-foreground">—</span>;
  const label = row.stateDescription ?? row.stateCode;
  const hex = colors[row.stateCode];
  if (!hex) {
    return (
      <span className="inline-flex items-center rounded-full border border-glass-strong bg-glass px-2 py-0.5 text-[11px] text-muted-foreground">
        {label}
      </span>
    );
  }
  return (
    <span
      className="inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium"
      style={{ backgroundColor: `${hex}22`, border: `1px solid ${hex}55`, color: hex }}
    >
      {label}
    </span>
  );
}

// ---- column model -----------------------------------------------------

type ColId =
  | "docNr"
  | "description"
  | "relation"
  | "status"
  | "startDate"
  | "endDate"
  | "term"
  | "totalExclVat"
  | "relationCode"
  | "invoicingPeriodicity"
  | "numberOfTerms"
  | "totalInclVat"
  | "docDate"
  | "stopDate"
  | "memo"
  | "syncedUtc";

type ColDef = {
  id: ColId;
  label: string;
  align: "left" | "right";
  defaultVisible: boolean;
  sortKey?: string;
  render: (r: AdsolutContractHeader) => React.ReactNode;
};

function formatDate(iso: string | null | undefined): string {
  if (!iso) return "—";
  try {
    return new Intl.DateTimeFormat(undefined, { year: "numeric", month: "short", day: "2-digit" }).format(new Date(iso));
  } catch {
    return iso;
  }
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

function formatMoney(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  try {
    return new Intl.NumberFormat("nl-BE", { style: "currency", currency: "EUR" }).format(value);
  } catch {
    return value.toFixed(2);
  }
}

const ALL_COLUMNS: ColDef[] = [
  {
    id: "docNr",
    label: "Doc nr",
    align: "left",
    defaultVisible: true,
    sortKey: "doc",
    render: (r) => <span className="font-mono text-xs text-foreground">{r.docNr ?? "—"}</span>,
  },
  {
    id: "description",
    label: "Title",
    align: "left",
    defaultVisible: true,
    sortKey: "title",
    render: (r) => (
      <span className="block max-w-[22rem] truncate text-foreground">{r.description || "—"}</span>
    ),
  },
  {
    id: "relation",
    label: "Relation",
    align: "left",
    defaultVisible: true,
    sortKey: "relation",
    render: (r) =>
      r.companyId ? (
        <Link
          to="/companies/$companyId"
          params={{ companyId: r.companyId }}
          className="text-primary hover:underline"
          onClick={(e) => e.stopPropagation()}
        >
          {r.companyName ?? r.customerName ?? "—"}
        </Link>
      ) : (
        <span className="text-foreground">{r.customerName ?? "—"}</span>
      ),
  },
  {
    id: "status",
    label: "Status",
    align: "left",
    defaultVisible: true,
    sortKey: "status",
    render: (r) => <ContractStatusPill row={r} />,
  },
  {
    id: "startDate",
    label: "Start",
    align: "left",
    defaultVisible: true,
    sortKey: "start",
    render: (r) => <span className="whitespace-nowrap">{formatDate(r.startDate)}</span>,
  },
  {
    id: "endDate",
    label: "End",
    align: "left",
    defaultVisible: true,
    sortKey: "end",
    render: (r) => <span className="whitespace-nowrap">{formatDate(r.endDate)}</span>,
  },
  {
    id: "term",
    label: "Term",
    align: "left",
    defaultVisible: true,
    sortKey: "term",
    render: (r) => (
      <span className="whitespace-nowrap text-muted-foreground">
        {r.periodicityLabel ?? r.periodicityCode ?? "—"}
      </span>
    ),
  },
  {
    id: "totalExclVat",
    label: "Total excl. VAT",
    align: "right",
    defaultVisible: true,
    sortKey: "total",
    render: (r) => (
      <span className="whitespace-nowrap tabular-nums text-foreground">{formatMoney(r.totalExclVat)}</span>
    ),
  },
  {
    id: "relationCode",
    label: "Relation code",
    align: "left",
    defaultVisible: false,
    render: (r) => <span className="font-mono text-xs text-muted-foreground">{r.relationCode ?? "—"}</span>,
  },
  {
    id: "invoicingPeriodicity",
    label: "Invoicing",
    align: "left",
    defaultVisible: false,
    render: (r) => (
      <span className="whitespace-nowrap text-muted-foreground">
        {r.invoicingPeriodicityLabel ?? r.invoicingPeriodicityCode ?? "—"}
      </span>
    ),
  },
  {
    id: "numberOfTerms",
    label: "Terms",
    align: "right",
    defaultVisible: false,
    render: (r) => (
      <span className="tabular-nums text-muted-foreground">{r.numberOfTerms ?? "—"}</span>
    ),
  },
  {
    id: "totalInclVat",
    label: "Total incl. VAT",
    align: "right",
    defaultVisible: false,
    render: (r) => (
      <span className="whitespace-nowrap tabular-nums text-muted-foreground">{formatMoney(r.totalInclVat)}</span>
    ),
  },
  {
    id: "docDate",
    label: "Doc date",
    align: "left",
    defaultVisible: false,
    render: (r) => <span className="whitespace-nowrap">{formatDate(r.docDate)}</span>,
  },
  {
    id: "stopDate",
    label: "Stop date",
    align: "left",
    defaultVisible: false,
    render: (r) => <span className="whitespace-nowrap">{formatDate(r.stopDate)}</span>,
  },
  {
    id: "memo",
    label: "Memo",
    align: "left",
    defaultVisible: false,
    render: (r) => (
      <span className="block max-w-[22rem] truncate text-muted-foreground">{r.memo || "—"}</span>
    ),
  },
  {
    id: "syncedUtc",
    label: "Synced",
    align: "left",
    defaultVisible: false,
    render: (r) => <span className="whitespace-nowrap text-muted-foreground/70">{formatDateTime(r.syncedUtc)}</span>,
  },
];

const COL_LABELS: Record<ColId, string> = Object.fromEntries(
  ALL_COLUMNS.map((c) => [c.id, c.label]),
) as Record<ColId, string>;

type ColConfig = { id: ColId; visible: boolean };

const DEFAULT_CONFIG: ColConfig[] = ALL_COLUMNS.map((c) => ({ id: c.id, visible: c.defaultVisible }));

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
    if (!seen.has(d.id)) merged.push({ id: d.id, visible: d.defaultVisible });
  }
  return merged.length > 0 ? merged : DEFAULT_CONFIG;
}

// ---- main page --------------------------------------------------------

export function ContractsOverviewPage() {
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [sortKey, setSortKey] = useState("start");
  const [sortDir, setSortDir] = useState<SortDir>("desc");

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

  const list = useQuery({
    queryKey: [...CONTRACTS_KEY, search, page, sortKey, sortDir],
    queryFn: () => contractsApi.list(search, page, PAGE_SIZE, sortKey, sortDir),
  });

  const sync = useMutation({
    mutationFn: () => contractsApi.sync(),
    onSuccess: () => toast.success("Contracts sync started — refresh in a moment for new data."),
    onError: () => toast.error("Could not start the contracts sync. Is the Adsolut Contracts pull enabled?"),
  });

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const colCount = visibleColumns.length + 2; // chevron + columns + resync
  const pageItems = list.data?.items ?? [];
  const statusColors = list.data?.statusColors ?? {};

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
    <StatusColorsContext.Provider value={statusColors}>
    <div className="flex flex-1 flex-col gap-4 p-4 sm:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <Link
            to="/contracts"
            className="flex h-10 w-10 items-center justify-center rounded-full border border-glass bg-glass text-muted-foreground transition-colors hover:text-foreground"
            aria-label="Back to Contracts"
          >
            <ChevronLeft className="h-4.5 w-4.5" />
          </Link>
          <div>
            <h1 className="text-display-md font-semibold text-foreground">Contracts overview</h1>
            <p className="text-xs text-muted-foreground">
              {total.toLocaleString()} contracts mirrored from Adsolut
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <div className="relative w-full max-w-xs">
            <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setPage(1);
              }}
              placeholder="Search relation, title, doc nr…"
              className="pl-9"
            />
          </div>
          <ColumnPicker config={effectiveConfig} onChange={persistConfig} />
          <Button
            size="sm"
            variant="ghost"
            className="gap-1.5"
            onClick={() => sync.mutate()}
            disabled={sync.isPending}
          >
            <RefreshCw className={cn("h-4 w-4", sync.isPending && "animate-spin")} />
            Sync contracts
          </Button>
        </div>
      </div>

      <div className="glass-panel flex-1 overflow-hidden">
        {list.isLoading ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : list.isError ? (
          <div className="p-6 text-sm text-rose-300">
            Could not load contracts. Refresh or check the integration status.
          </div>
        ) : pageItems.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
            <FileSignature className="h-6 w-6 text-muted-foreground" />
            <p className="text-sm text-muted-foreground">
              {search.trim()
                ? "No contracts match your search."
                : "No contracts mirrored yet. An admin enables the pull under Settings → Integrations → Adsolut."}
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
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
                            (sortDir === "asc" ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />)}
                        </span>
                      </th>
                    );
                  })}
                  <th className="w-10 px-3 py-2.5" />
                </tr>
              </thead>
              <tbody>
                {pageItems.map((c) => (
                  <ContractRow
                    key={c.id}
                    contract={c}
                    columns={visibleColumns}
                    colCount={colCount}
                    expanded={expandedId === c.id}
                    onToggle={() => setExpandedId((cur) => (cur === c.id ? null : c.id))}
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
    </StatusColorsContext.Provider>
  );
}

// ---- row --------------------------------------------------------------

function ContractRow({
  contract,
  columns,
  colCount,
  expanded,
  onToggle,
}: {
  contract: AdsolutContractHeader;
  columns: ColDef[];
  colCount: number;
  expanded: boolean;
  onToggle: () => void;
}) {
  const qc = useQueryClient();
  const detail = useQuery({
    queryKey: ["contracts", "overview", "detail", contract.id],
    queryFn: () => contractsApi.detail(contract.id),
    enabled: expanded,
  });

  const resync = useMutation({
    mutationFn: () => contractsApi.resync(contract.id),
    onSuccess: () => {
      toast.success(`Contract ${contract.docNr ?? ""} resynced`);
      qc.invalidateQueries({ queryKey: ["contracts", "overview", "detail", contract.id] });
      qc.invalidateQueries({ queryKey: CONTRACTS_KEY });
    },
    onError: () => toast.error("Resync failed"),
  });

  return (
    <>
      <tr
        className={cn(
          "cursor-pointer border-b border-glass transition-colors hover:bg-glass-hover",
          expanded && "bg-glass-hover",
        )}
        onClick={onToggle}
      >
        <td className="px-3 py-2.5 text-muted-foreground">
          {expanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
        </td>
        {columns.map((col) => (
          <td key={col.id} className={cn("px-3 py-2.5", col.align === "right" && "text-right")}>
            {col.render(contract)}
          </td>
        ))}
        <td className="px-3 py-2.5 text-right">
          <Button
            size="sm"
            variant="ghost"
            className="h-7 w-7 p-0"
            aria-label="Resync this contract"
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
        <tr className="border-b border-glass bg-glass">
          <td colSpan={colCount} className="px-6 py-4">
            {detail.isLoading ? (
              <Skeleton className="h-20 w-full" />
            ) : detail.isError || !detail.data ? (
              <p className="text-xs text-rose-300">Could not load contract detail.</p>
            ) : (
              <ContractDetail data={detail.data} />
            )}
          </td>
        </tr>
      )}
    </>
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
