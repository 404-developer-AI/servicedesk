import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowDown,
  ArrowUp,
  ChevronDown,
  ChevronRight,
  Columns3,
  GripVertical,
  RefreshCw,
  Search,
  ShoppingCart,
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
  ordersApi,
  preferencesApi,
  type AdsolutOrderHeader,
} from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import { OrderDetail } from "./OrderDetail";
import { formatDate, formatMoney } from "./orderFormat";

const PAGE_SIZE = 50;
const ORDERS_KEY = ["orders", "list"] as const;
// Per-agent column config (visibility + order) — persisted server-side via the
// generic workspace-preferences store, so it follows the agent across devices.
const COLUMNS_PREF_KEY = "workspace:adsolut-orders-columns";

type SortDir = "asc" | "desc";

// ---- column model -----------------------------------------------------

type ColId = "doc" | "docnr" | "date" | "customer" | "description" | "status" | "representative" | "total" | "delivery";

type ColDef = {
  id: ColId;
  label: string;
  align: "left" | "right";
  defaultVisible: boolean;
  /** Server sort key — present = the header is clickable to sort. */
  sortKey?: string;
  render: (r: AdsolutOrderHeader) => React.ReactNode;
};

const ALL_COLUMNS: ColDef[] = [
  {
    id: "doc",
    label: "Doc",
    align: "left",
    defaultVisible: true,
    render: (r) => <span className="font-mono text-xs text-muted-foreground">{r.bookCode ?? "—"}</span>,
  },
  {
    id: "docnr",
    label: "Doc nr",
    align: "left",
    defaultVisible: true,
    sortKey: "doc",
    render: (r) => <span className="font-mono text-xs text-foreground">{r.docNr ?? "—"}</span>,
  },
  {
    id: "date",
    label: "Date",
    align: "left",
    defaultVisible: true,
    sortKey: "date",
    render: (r) => <span className="whitespace-nowrap">{formatDate(r.orderDate)}</span>,
  },
  {
    id: "customer",
    label: "Relation",
    align: "left",
    defaultVisible: true,
    sortKey: "customer",
    render: (r) => (
      <>
        <span className="text-foreground">{r.customerName ?? "—"}</span>
        {r.customerCode && <span className="ml-1.5 text-[11px] text-muted-foreground/60">{r.customerCode}</span>}
      </>
    ),
  },
  {
    id: "description",
    label: "Description",
    align: "left",
    defaultVisible: true,
    render: (r) => <span className="block max-w-[22rem] truncate text-muted-foreground">{r.remark || "—"}</span>,
  },
  {
    id: "status",
    label: "Status",
    align: "left",
    defaultVisible: true,
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
    id: "representative",
    label: "Representative",
    align: "left",
    defaultVisible: false,
    render: (r) => <span className="whitespace-nowrap text-muted-foreground">{r.representativeName ?? "—"}</span>,
  },
  {
    id: "total",
    label: "Total (excl. VAT)",
    align: "right",
    defaultVisible: false,
    sortKey: "total",
    render: (r) => (
      <span className="whitespace-nowrap tabular-nums text-foreground">{formatMoney(r.totalExclVat, r.currencyIso)}</span>
    ),
  },
  {
    id: "delivery",
    label: "Req. delivery",
    align: "left",
    defaultVisible: false,
    sortKey: "delivery",
    render: (r) => <span className="whitespace-nowrap">{formatDate(r.requestedDeliveryDate)}</span>,
  },
];

const COL_LABELS: Record<ColId, string> = Object.fromEntries(
  ALL_COLUMNS.map((c) => [c.id, c.label]),
) as Record<ColId, string>;

type ColConfig = { id: ColId; visible: boolean };

const DEFAULT_CONFIG: ColConfig[] = ALL_COLUMNS.map((c) => ({ id: c.id, visible: c.defaultVisible }));

/// Merge a saved config with ALL_COLUMNS: keep known ids in their saved order,
/// drop unknown ids, and append any columns added since the config was saved
/// (using each column's defaultVisible) so a new column never silently breaks.
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

/// Orders overview (navbar → Assets → Orders). Mounted only when the user has
/// the `adsolut_orders_enabled` feature flag (Sidebar + backend RequireAgent).
/// Lists the mirrored Adsolut orders with expandable detail lines, per-agent
/// column visibility/order, server-side sorting, a global "Sync orders"
/// trigger and a per-row resync. The admin's display status filter (Settings →
/// Integrations → Adsolut) narrows what is shown here.
export function OrdersPage() {
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [sortKey, setSortKey] = useState("date");
  const [sortDir, setSortDir] = useState<SortDir>("desc");

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

  const list = useQuery({
    queryKey: [...ORDERS_KEY, search, page, sortKey, sortDir],
    queryFn: () => ordersApi.list(search, page, PAGE_SIZE, sortKey, sortDir),
  });

  const sync = useMutation({
    mutationFn: () => ordersApi.sync(),
    onSuccess: () => toast.success("Orders sync started — refresh in a moment for new data."),
    onError: () => toast.error("Could not start the orders sync. Is the Adsolut Orders pull enabled?"),
  });

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const colCount = visibleColumns.length + 2; // chevron + columns + resync
  const pageItems = list.data?.items ?? [];

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
    <div className="flex flex-1 flex-col gap-4 p-4 sm:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <div className="flex h-10 w-10 items-center justify-center rounded-full border border-glass bg-glass">
            <ShoppingCart className="h-4.5 w-4.5 text-muted-foreground" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold text-foreground">Orders</h1>
            <p className="text-xs text-muted-foreground">
              {total.toLocaleString()} orders mirrored from Adsolut
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
              placeholder="Search relation, description, doc nr…"
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
            Sync orders
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
            Could not load orders. Refresh or check the integration status.
          </div>
        ) : pageItems.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
            <ShoppingCart className="h-6 w-6 text-muted-foreground" />
            <p className="text-sm text-muted-foreground">
              {search.trim()
                ? "No orders match your search."
                : "No orders mirrored yet. An admin enables the pull under Settings → Integrations → Adsolut."}
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
                {pageItems.map((o) => (
                  <OrderRow
                    key={o.id}
                    order={o}
                    columns={visibleColumns}
                    colCount={colCount}
                    expanded={expandedId === o.id}
                    onToggle={() => setExpandedId((cur) => (cur === o.id ? null : o.id))}
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
  );
}

// ---- row --------------------------------------------------------------

function OrderRow({
  order,
  columns,
  colCount,
  expanded,
  onToggle,
}: {
  order: AdsolutOrderHeader;
  columns: ColDef[];
  colCount: number;
  expanded: boolean;
  onToggle: () => void;
}) {
  const qc = useQueryClient();
  const detail = useQuery({
    queryKey: ["orders", "detail", order.id],
    queryFn: () => ordersApi.detail(order.id),
    enabled: expanded,
  });

  const resync = useMutation({
    mutationFn: () => ordersApi.resync(order.id),
    onSuccess: () => {
      toast.success(`Order ${order.docNr ?? ""} resynced`);
      qc.invalidateQueries({ queryKey: ["orders", "detail", order.id] });
      qc.invalidateQueries({ queryKey: ORDERS_KEY });
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
            {col.render(order)}
          </td>
        ))}
        <td className="px-3 py-2.5 text-right">
          <Button
            size="sm"
            variant="ghost"
            className="h-7 w-7 p-0"
            aria-label="Resync this order"
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
              <p className="text-xs text-rose-300">Could not load order detail.</p>
            ) : (
              <OrderDetail data={detail.data} />
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
