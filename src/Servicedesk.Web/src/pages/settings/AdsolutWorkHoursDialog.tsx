import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { AlertTriangle, Clock, RefreshCw, Search } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  adsolutCatalogueProductsApi,
  type AdsolutCatalogueProduct,
} from "@/lib/api";
import { cn } from "@/lib/utils";

const PAGE_SIZE = 50;
const STATE_KEY = ["timesheet", "adsolut", "catalogue-products", "state"] as const;
const LIST_KEY = ["timesheet", "adsolut", "catalogue-products", "list"] as const;
// The Adsolut tab's receipts query — invalidated after a flag change so the VK
// Werkuren column reflects the new selection without a manual refresh.
const RECEIPTS_KEY = ["timesheet", "adsolut", "receipts"] as const;

type WorkHoursFilter = "all" | "yes" | "no";

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

/// Scrollable pop-up to manage which Adsolut catalogue products count as
/// billable work hours (VK Werkuren). Search + filter (code/name, active,
/// work-hours), toggle the flag per product, and trigger a catalogue sync. The
/// catalogue shares the sales-receipt opt-in, so when that's off the manager
/// shows a notice instead of data.
export function AdsolutWorkHoursDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient();
  const [search, setSearch] = React.useState("");
  const [page, setPage] = React.useState(1);
  const [activeOnly, setActiveOnly] = React.useState(false);
  const [workHours, setWorkHours] = React.useState<WorkHoursFilter>("all");

  // Reset paging whenever a filter changes.
  React.useEffect(() => {
    setPage(1);
  }, [search, activeOnly, workHours]);

  const state = useQuery({
    queryKey: STATE_KEY,
    queryFn: () => adsolutCatalogueProductsApi.state(),
    enabled: open,
  });

  const list = useQuery({
    queryKey: [...LIST_KEY, search, page, activeOnly, workHours],
    queryFn: () =>
      adsolutCatalogueProductsApi.list(search, page, PAGE_SIZE, "code", "asc", activeOnly, workHours),
    enabled: open,
  });

  const setFlag = useMutation({
    mutationFn: (vars: { id: string; value: boolean }) =>
      adsolutCatalogueProductsApi.setWorkHours(vars.id, vars.value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: LIST_KEY });
      qc.invalidateQueries({ queryKey: STATE_KEY });
      qc.invalidateQueries({ queryKey: RECEIPTS_KEY });
    },
    onError: () => toast.error("Could not update the work-hours flag"),
  });

  const sync = useMutation({
    mutationFn: () => adsolutCatalogueProductsApi.sync(),
    onSuccess: () => {
      toast.success("Catalogue sync requested — refresh in a moment");
      // Give the worker a beat, then refresh the state/list.
      window.setTimeout(() => {
        qc.invalidateQueries({ queryKey: STATE_KEY });
        qc.invalidateQueries({ queryKey: LIST_KEY });
      }, 1500);
    },
    onError: () => toast.error("Could not request a catalogue sync"),
  });

  const enabled = state.data?.enabled ?? true;
  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const items = list.data?.items ?? [];

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="flex max-h-[85vh] flex-col sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Work-hours articles</DialogTitle>
          <DialogDescription>
            Tick the Adsolut products that represent billable work hours. The Timesheet → Adsolut
            "VK Werkuren" total — and the registered-hours match — sums only the receipt lines whose
            product is ticked here, so hardware no longer skews the comparison.
          </DialogDescription>
        </DialogHeader>

        {/* Sync + state line */}
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground">
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
            <span>
              <span className="text-foreground">{state.data?.totalMirrored ?? 0}</span> products mirrored
            </span>
            <span>
              <span className="text-foreground">{state.data?.workHoursCount ?? 0}</span> flagged as work hours
            </span>
            <span>Last sync: {formatDateTime(state.data?.lastDeltaSyncUtc)}</span>
            {state.data?.lastError && (
              <span className="text-rose-300">Last error: {state.data.lastError}</span>
            )}
          </div>
          <Button
            size="sm"
            variant="ghost"
            className="h-7 gap-1.5"
            disabled={!enabled || sync.isPending}
            onClick={() => sync.mutate()}
          >
            <RefreshCw className={cn("h-3.5 w-3.5", sync.isPending && "animate-spin")} />
            Sync now
          </Button>
        </div>

        {!enabled ? (
          <div className="flex flex-col items-center justify-center gap-2 rounded-md border border-amber-400/20 bg-amber-500/[0.06] p-8 text-center">
            <AlertTriangle className="h-6 w-6 text-amber-300" />
            <p className="text-sm text-foreground">Sales-receipt mirroring is off.</p>
            <p className="max-w-md text-xs text-muted-foreground">
              The product catalogue shares the sales-receipts toggle. Enable it under{" "}
              <span className="text-foreground/80">Settings → Integrations → Adsolut</span>, then
              sync the catalogue here.
            </p>
          </div>
        ) : (
          <>
            {/* Filters */}
            <div className="flex flex-wrap items-center gap-2">
              <div className="relative min-w-0 flex-1">
                <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
                <Input
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                  placeholder="Search code or name…"
                  className="h-8 pl-9 text-sm"
                />
              </div>
              <Select value={workHours} onValueChange={(v) => setWorkHours(v as WorkHoursFilter)}>
                <SelectTrigger className="h-8 w-40 text-xs">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="all">All products</SelectItem>
                  <SelectItem value="yes">Work hours only</SelectItem>
                  <SelectItem value="no">Not work hours</SelectItem>
                </SelectContent>
              </Select>
              <Select value={activeOnly ? "active" : "all"} onValueChange={(v) => setActiveOnly(v === "active")}>
                <SelectTrigger className="h-8 w-32 text-xs">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="all">All states</SelectItem>
                  <SelectItem value="active">Active only</SelectItem>
                </SelectContent>
              </Select>
            </div>

            {/* Scrollable list */}
            <div className="min-h-0 flex-1 overflow-auto rounded-md border border-glass">
              {list.isLoading ? (
                <div className="space-y-2 p-3">
                  <Skeleton className="h-8 w-full" />
                  <Skeleton className="h-8 w-full" />
                  <Skeleton className="h-8 w-full" />
                </div>
              ) : list.isError ? (
                <p className="p-6 text-sm text-rose-300">Could not load the catalogue.</p>
              ) : items.length === 0 ? (
                <div className="flex flex-col items-center justify-center gap-2 p-10 text-center">
                  <Clock className="h-5 w-5 text-muted-foreground" />
                  <p className="text-sm text-muted-foreground">
                    {state.data?.totalMirrored === 0
                      ? "No products mirrored yet. Click “Sync now”."
                      : "No products match your filters."}
                  </p>
                </div>
              ) : (
                <table className="w-full text-sm">
                  <thead className="sticky top-0 z-10 bg-background/95 backdrop-blur">
                    <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                      <th className="px-3 py-2 font-medium">Code</th>
                      <th className="px-3 py-2 font-medium">Name</th>
                      <th className="px-3 py-2 font-medium">State</th>
                      <th className="px-3 py-2 text-center font-medium">Work hours</th>
                    </tr>
                  </thead>
                  <tbody>
                    {items.map((p) => (
                      <ProductRow
                        key={p.id}
                        product={p}
                        pending={setFlag.isPending && setFlag.variables?.id === p.id}
                        onToggle={(value) => setFlag.mutate({ id: p.id, value })}
                      />
                    ))}
                  </tbody>
                </table>
              )}
            </div>

            {/* Footer / pagination */}
            <div className="flex items-center justify-between text-xs text-muted-foreground">
              <span>{total.toLocaleString()} products</span>
              {totalPages > 1 && (
                <div className="flex items-center gap-2">
                  <span>
                    Page {page} / {totalPages}
                  </span>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2"
                    onClick={() => setPage((p) => Math.max(1, p - 1))}
                    disabled={page <= 1 || list.isFetching}
                  >
                    Previous
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2"
                    onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                    disabled={page >= totalPages || list.isFetching}
                  >
                    Next
                  </Button>
                </div>
              )}
            </div>
          </>
        )}
      </DialogContent>
    </Dialog>
  );
}

function ProductRow({
  product,
  pending,
  onToggle,
}: {
  product: AdsolutCatalogueProduct;
  pending: boolean;
  onToggle: (value: boolean) => void;
}) {
  return (
    <tr className="border-b border-glass last:border-b-0 hover:bg-glass-hover">
      <td className="px-3 py-2 font-mono text-xs text-muted-foreground">{product.code ?? "—"}</td>
      <td className="px-3 py-2">
        <span className="text-foreground">{product.name ?? "—"}</span>
        {product.serviceProduct && (
          <span className="ml-1.5 text-[10px] uppercase tracking-wider text-muted-foreground/50">
            service
          </span>
        )}
      </td>
      <td className="px-3 py-2">
        {product.blocked ? (
          <span className="text-[11px] text-rose-300">blocked</span>
        ) : product.isActive ? (
          <span className="text-[11px] text-emerald-300">active</span>
        ) : (
          <span className="text-[11px] text-muted-foreground/60">inactive</span>
        )}
      </td>
      <td className="px-3 py-2 text-center">
        <input
          type="checkbox"
          checked={product.countsAsWorkHours}
          disabled={pending}
          onChange={(e) => onToggle(e.target.checked)}
          aria-label={`Toggle work-hours for ${product.code ?? product.name ?? "product"}`}
          className="h-4 w-4 rounded border border-glass-strong bg-glass accent-purple-400 disabled:opacity-50"
        />
      </td>
    </tr>
  );
}
