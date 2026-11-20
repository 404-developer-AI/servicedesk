import * as React from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useRouterState } from "@tanstack/react-router";
import { toast } from "sonner";
import {
  ArrowDown,
  ArrowUp,
  CalendarClock,
  CircleAlert,
  Layers,
  PackageCheck,
  PackageSearch,
  RefreshCw,
  Search,
  SearchCheck,
  Ticket,
  Truck,
  X,
} from "lucide-react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { useDebounced } from "@/hooks/useDebounced";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { apiErrorMessage } from "@/lib/api";
import { formatDate } from "@/lib/dateFormat";
import { formatMoney, formatNumber, formatTimeAgo } from "@/lib/format";
import { cn } from "@/lib/utils";
import {
  orderManagementApi,
  type TdOrderBucket,
  type TdOrderHeader,
} from "@/lib/order-management-api";
import { EtaLabel, OrderStatusChip } from "./orderUi";
import { OrderDetailPanel } from "./OrderDetailPanel";

const PAGE_SIZE = 50;
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const BUCKETS: { id: TdOrderBucket; label: string }[] = [
  { id: "active", label: "Active" },
  { id: "late", label: "Late" },
  { id: "due", label: "Due this week" },
  { id: "open", label: "Open" },
  { id: "investigation", label: "Investigation" },
  { id: "shipping", label: "Shipping" },
  { id: "completed", label: "Completed" },
  { id: "rejected", label: "Rejected" },
  { id: "all", label: "All" },
];

const EMPTY_TEXT: Record<TdOrderBucket, string> = {
  active: "No open orders at TD SYNNEX right now.",
  late: "Nothing is late. Orders whose expected ship date has passed show up here.",
  due: "Nothing is expected to ship in the next 7 days.",
  open: "No open orders.",
  investigation: "No orders under investigation.",
  shipping: "No orders being shipped.",
  completed: "No completed orders in the mirror yet.",
  rejected: "No rejected orders.",
  all: "No orders synced yet. An admin connects TD SYNNEX under Settings → Integrations.",
};

type SortKey = "created" | "eta" | "orderNumber" | "customerPo" | "shipTo" | "status" | "total" | "changed";

function orderIdFrom(search: string): string | null {
  const id = new URLSearchParams(search).get("order");
  return id && UUID_RE.test(id) ? id : null;
}

/// v0.1.59 — Order Management: the TD SYNNEX order dashboard. KPI tiles
/// (each one filters the list), status buckets, search across order / PO /
/// ship-to / part numbers, and a detail dialog. `?order=<id>` opens one
/// (global search, trigger deep-link). Flag-gated route and API.
export function OrderManagementPage() {
  const zone = useServerTimeZone();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const [bucket, setBucket] = React.useState<TdOrderBucket>("active");
  const [search, setSearch] = React.useState("");
  const [sort, setSort] = React.useState<{ key: SortKey; dir: "asc" | "desc" }>({ key: "eta", dir: "asc" });
  const [page, setPage] = React.useState(1);
  const [openId, setOpenId] = React.useState<string | null>(() => orderIdFrom(window.location.search));
  const searchStr = useRouterState({ select: (s) => s.location.searchStr });
  React.useEffect(() => {
    const id = orderIdFrom(searchStr);
    if (id) setOpenId(id);
  }, [searchStr]);
  const closeOrder = () => {
    setOpenId(null);
    if (orderIdFrom(window.location.search)) void navigate({ to: "/order-management", replace: true });
  };

  const q = useDebounced(search, 250);
  React.useEffect(() => setPage(1), [bucket, q, sort]);

  const summary = useQuery({
    queryKey: ["order-management", "summary"],
    queryFn: orderManagementApi.summary,
    staleTime: 30_000,
    refetchInterval: 120_000,
  });
  const listParams = { search: q.trim() || undefined, bucket, sort: sort.key, dir: sort.dir, page, pageSize: PAGE_SIZE };
  const list = useQuery({
    queryKey: ["order-management", "list", listParams],
    queryFn: () => orderManagementApi.list(listParams),
    placeholderData: keepPreviousData,
    staleTime: 30_000,
    refetchInterval: 120_000,
  });

  const sync = useMutation({
    mutationFn: orderManagementApi.sync,
    onSuccess: (r) => {
      if (r.status === "busy") toast.message("A sync is already running.");
      else if (r.success) toast.success(r.changed > 0 ? `Synced — ${r.changed} order${r.changed === 1 ? "" : "s"} changed` : "Synced — no changes");
      else toast.error("The sync did not complete. An admin can see why under Settings → Integrations → TD SYNNEX.");
      void qc.invalidateQueries({ queryKey: ["order-management"] });
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "Sync failed"),
  });

  const stats = summary.data?.stats;
  const syncInfo = summary.data?.sync;
  const items = list.data?.items ?? [];
  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  const toggleSort = (key: SortKey) =>
    setSort((s) => (s.key === key ? { key, dir: s.dir === "asc" ? "desc" : "asc" } : { key, dir: key === "eta" ? "asc" : "desc" }));

  const kpis: { id: TdOrderBucket; label: string; value: number | undefined; icon: React.ComponentType<{ className?: string }>; hint: string; alert?: boolean }[] = [
    { id: "open", label: "Open", value: stats?.open, icon: PackageSearch, hint: "Placed, not yet shipping" },
    { id: "investigation", label: "Investigation", value: stats?.investigation, icon: SearchCheck, hint: "TD SYNNEX is checking" },
    { id: "shipping", label: "Shipping", value: stats?.shipping, icon: Truck, hint: "Partly or fully on its way" },
    { id: "late", label: "Late", value: stats?.late, icon: CircleAlert, hint: "Ship date passed", alert: (stats?.late ?? 0) > 0 },
    { id: "due", label: "Due this week", value: stats?.dueThisWeek, icon: CalendarClock, hint: "Expected in the next 7 days" },
    { id: "completed", label: "Completed", value: stats?.completedLast30Days, icon: PackageCheck, hint: "Last 30 days" },
  ];

  return (
    <div className="sd-om flex min-h-0 w-full flex-1 flex-col gap-6">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">Order Management</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            Your TD SYNNEX orders — what is still open, when it ships and which ticket it belongs to.
            Link an order from a ticket to follow it there.
          </p>
        </div>
        <div className="flex items-center gap-3">
          {syncInfo && (
            <span className={cn("text-xs", syncInfo.healthy ? "text-muted-foreground" : "text-amber-700 dark:text-amber-300")}>
              {!syncInfo.enabled
                ? "Sync is turned off"
                : syncInfo.lastSuccessUtc
                  ? `${syncInfo.healthy ? "Synced" : "Last good sync"} ${formatTimeAgo(syncInfo.lastSuccessUtc)}`
                  : "Not synced yet"}
              {stats && stats.openValue > 0 && (
                <span className="text-muted-foreground"> · open value {formatMoney(stats.openValue, stats.currency)}</span>
              )}
            </span>
          )}
          <Button size="sm" variant="outline" className="gap-1.5" onClick={() => sync.mutate()} disabled={sync.isPending || !syncInfo?.enabled}>
            <RefreshCw className={cn("h-3.5 w-3.5", sync.isPending && "animate-spin")} />
            Sync now
          </Button>
        </div>
      </header>

      <section aria-label="Order totals" className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-6">
        {kpis.map((k) => {
          const Icon = k.icon;
          const selected = bucket === k.id;
          return (
            <button
              key={k.id}
              type="button"
              onClick={() => setBucket(k.id)}
              className={cn(
                "glass-card sd-om-kpi flex flex-col items-start gap-1 p-4 text-left transition-colors hover:bg-glass-hover",
                selected && "ring-1 ring-primary/40",
              )}
            >
              <span className="flex w-full items-center justify-between text-xs text-muted-foreground">
                {k.label}
                <Icon className={cn("h-4 w-4", k.alert ? "text-rose-600 dark:text-rose-300" : "text-muted-foreground/70")} />
              </span>
              {summary.isLoading ? (
                <Skeleton className="h-7 w-12" />
              ) : (
                <span className={cn("text-2xl font-semibold tabular-nums", k.alert ? "text-rose-700 dark:text-rose-300" : "text-foreground")}>
                  {formatNumber(k.value ?? 0)}
                </span>
              )}
              <span className="text-[11px] text-muted-foreground/80">{k.hint}</span>
            </button>
          );
        })}
      </section>

      <div className="flex flex-col gap-3">
        <div className="glass-panel flex w-fit flex-wrap items-center gap-1 p-1" role="tablist" aria-label="Status">
          {BUCKETS.map((b) => (
            <button
              key={b.id}
              type="button"
              role="tab"
              aria-selected={bucket === b.id}
              onClick={() => setBucket(b.id)}
              className={cn(
                "inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm transition-colors",
                bucket === b.id
                  ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
                  : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
              )}
            >
              {b.id === "late" && <CircleAlert className="h-3.5 w-3.5" />}
              {b.id === "all" && <Layers className="h-3.5 w-3.5" />}
              {b.label}
              {b.id === "late" && (stats?.late ?? 0) > 0 && (
                <span className="rounded-md bg-rose-500/15 px-1.5 text-[11px] tabular-nums text-rose-700 dark:text-rose-300">{stats!.late}</span>
              )}
            </button>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <div className="relative min-w-56 flex-1 sm:max-w-md">
            <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search order, PO, ship-to, part number, delivery note…"
              className="pl-9"
            />
          </div>
          {search && (
            <Button variant="ghost" size="sm" onClick={() => setSearch("")}>
              <X className="mr-1 h-3.5 w-3.5" /> Clear
            </Button>
          )}
        </div>
      </div>

      <div className="glass-panel min-h-64 flex-1 overflow-auto">
        {list.isLoading ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : list.isError ? (
          <div className="p-6 text-sm text-rose-600 dark:text-rose-300">Could not load the orders. Refresh to try again.</div>
        ) : items.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
            <PackageSearch className="h-6 w-6 text-muted-foreground" />
            <p className="max-w-md text-sm text-muted-foreground">{q.trim() ? "No orders match this search." : EMPTY_TEXT[bucket]}</p>
          </div>
        ) : (
          <div>
            <table className="w-full text-sm">
              <thead className="sticky top-0 z-10 bg-popover">
                <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                  <SortTh label="Order" k="orderNumber" sort={sort} onSort={toggleSort} className="pl-4" />
                  <SortTh label="Customer PO" k="customerPo" sort={sort} onSort={toggleSort} />
                  <SortTh label="Ship to" k="shipTo" sort={sort} onSort={toggleSort} />
                  <SortTh label="Status" k="status" sort={sort} onSort={toggleSort} />
                  <SortTh label="Expected" k="eta" sort={sort} onSort={toggleSort} />
                  <SortTh label="Ordered" k="created" sort={sort} onSort={toggleSort} />
                  <SortTh label="Net" k="total" sort={sort} onSort={toggleSort} align="right" />
                  <th className="px-4 py-2.5 font-medium">Tickets</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-glass">
                {items.map((o) => (
                  <OrderRow key={o.id} order={o} zone={zone} onOpen={() => setOpenId(o.id)} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {total > PAGE_SIZE && (
        <div className="flex items-center justify-between text-xs text-muted-foreground">
          <span>Page {page} of {totalPages} · {formatNumber(total)} orders</span>
          <div className="flex items-center gap-2">
            <Button size="sm" variant="ghost" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page <= 1 || list.isFetching}>
              Previous
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setPage((p) => Math.min(totalPages, p + 1))} disabled={page >= totalPages || list.isFetching}>
              Next
            </Button>
          </div>
        </div>
      )}

      <OrderDetailDialog orderId={openId} onClose={closeOrder} />
    </div>
  );
}

function SortTh({
  label, k, sort, onSort, align, className,
}: {
  label: string;
  k: SortKey;
  sort: { key: SortKey; dir: "asc" | "desc" };
  onSort: (k: SortKey) => void;
  align?: "right";
  className?: string;
}) {
  const active = sort.key === k;
  return (
    <th className={cn("px-3 py-2.5 font-medium", align === "right" && "text-right", className)} aria-sort={active ? (sort.dir === "asc" ? "ascending" : "descending") : "none"}>
      <button type="button" onClick={() => onSort(k)} className={cn("inline-flex items-center gap-1 uppercase tracking-wider hover:text-foreground", active && "text-foreground")}>
        {label}
        {active && (sort.dir === "asc" ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />)}
      </button>
    </th>
  );
}

function OrderRow({ order: o, zone, onOpen }: { order: TdOrderHeader; zone: string | null; onOpen: () => void }) {
  return (
    <tr
      className={cn("sd-om-row cursor-pointer transition-colors hover:bg-glass-hover", o.isLate && "sd-om-row-late")}
      onClick={onOpen}
      onKeyDown={(e) => { if (e.key === "Enter") onOpen(); }}
      tabIndex={0}
    >
      <td className="whitespace-nowrap py-2.5 pl-4 pr-3">
        <div className="font-medium tabular-nums text-foreground">{o.orderNumber}</div>
        <div className="text-[11px] text-muted-foreground">{o.lineCount} line{o.lineCount === 1 ? "" : "s"}</div>
      </td>
      <td className="max-w-40 truncate px-3 py-2.5 text-foreground">{o.customerOrderNumber ?? "—"}</td>
      <td className="max-w-56 px-3 py-2.5">
        <div className="truncate text-foreground">{o.shipToName ?? "—"}</div>
        {o.shipToCity && <div className="truncate text-[11px] text-muted-foreground">{o.shipToCity}{o.shipToCountry ? `, ${o.shipToCountry}` : ""}</div>}
      </td>
      <td className="px-3 py-2.5"><OrderStatusChip status={o.status} /></td>
      <td className="whitespace-nowrap px-3 py-2.5"><EtaLabel order={o} compact /></td>
      <td className="whitespace-nowrap px-3 py-2.5 text-muted-foreground">{o.createdUtc ? formatDate(o.createdUtc, zone) : "—"}</td>
      <td className="whitespace-nowrap px-3 py-2.5 text-right tabular-nums text-foreground">{formatMoney(o.totalNet, o.currency)}</td>
      <td className="px-4 py-2.5">
        {o.linkedTicketCount > 0 ? (
          <span className="inline-flex items-center gap-1 text-xs text-foreground">
            <Ticket className="h-3.5 w-3.5 text-muted-foreground" /> {o.linkedTicketCount}
          </span>
        ) : (
          <span className="text-xs text-muted-foreground/60">—</span>
        )}
      </td>
    </tr>
  );
}

function OrderDetailDialog({ orderId, onClose }: { orderId: string | null; onClose: () => void }) {
  const detail = useQuery({
    queryKey: ["order-management", "detail", orderId],
    queryFn: () => orderManagementApi.detail(orderId!),
    enabled: !!orderId,
  });
  const h = detail.data?.order.header;
  return (
    <Dialog open={!!orderId} onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="flex max-h-[90vh] w-[95vw] max-w-5xl flex-col">
        <DialogHeader>
          <DialogTitle className="truncate">
            {h ? `Order ${h.orderNumber}${h.shipToName ? ` · ${h.shipToName}` : ""}` : "Order"}
          </DialogTitle>
          <DialogDescription>TD SYNNEX order with its lines, deliveries and change history.</DialogDescription>
        </DialogHeader>
        {detail.isLoading ? (
          <Skeleton className="h-48 w-full" />
        ) : detail.isError || !detail.data ? (
          <p className="py-6 text-sm text-rose-600 dark:text-rose-300">Could not load this order.</p>
        ) : (
          <div className="min-h-0 flex-1 overflow-y-auto pr-1">
            <OrderDetailPanel
              detail={detail.data.order}
              linkedTickets={detail.data.linkedTickets}
              hiddenLinkedTicketCount={detail.data.hiddenLinkedTicketCount}
            />
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
