import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ChevronDown, Link2, Package, Plus, Search, Unlink } from "lucide-react";
import { useAuth } from "@/auth/authStore";
import { useDebounced } from "@/hooks/useDebounced";
import { apiErrorMessage } from "@/lib/api";
import { cn } from "@/lib/utils";
import {
  orderManagementApi,
  ticketOrdersKey,
  type TdOrderDetail,
  type TdOrderHeader,
} from "@/lib/order-management-api";
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
import { EtaLabel, OrderStatusChip } from "@/pages/order-management/orderUi";
import { TONE_CLASSES, formatShipDate, orderTone, statusMeta } from "@/pages/order-management/orderFormat";
import { OrderDetailPanel } from "@/pages/order-management/OrderDetailPanel";

/// v0.1.59 — the ticket's TD SYNNEX order chip, next to the SLA pill. Without
/// linked orders it is a quiet "Link order" entry point; with orders it shows
/// the most urgent one (late > rejected > investigation > open > shipping >
/// completed) and its expected ship date, and opens a popup with everything
/// about every linked order. Only for users with the Order Management flag —
/// the API enforces the same flag and the ticket's queue access.
export function TicketOrdersButton({ ticketId }: { ticketId: string }) {
  const { user } = useAuth();
  const enabled = !!user?.orderManagementEnabled;
  const [open, setOpen] = React.useState(false);

  const linked = useQuery({
    queryKey: ticketOrdersKey(ticketId),
    queryFn: () => orderManagementApi.forTicket(ticketId),
    enabled,
    staleTime: 60_000,
  });

  if (!enabled) return null;
  const orders = linked.data?.items ?? [];
  const headline = pickHeadline(orders.map((o) => o.header));

  return (
    <>
      {headline ? (
        <button
          type="button"
          onClick={() => setOpen(true)}
          className={cn(
            "sd-ticket-orders-chip inline-flex shrink-0 items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium tabular-nums transition-opacity hover:opacity-85",
            TONE_CLASSES[orderTone(headline)],
          )}
          title={chipTitle(orders.map((o) => o.header))}
        >
          <Package className="h-3.5 w-3.5" aria-hidden />
          {orders.length > 1 && <span>{orders.length} orders ·</span>}
          <span>{headline.isLate ? "Late" : statusMeta(headline.status).label}</span>
          {headline.firstShipDate && headline.status !== "COMPLETED" && (
            <span className="opacity-80">· {formatShipDate(headline.firstShipDate)}</span>
          )}
        </button>
      ) : (
        <button
          type="button"
          onClick={() => setOpen(true)}
          disabled={linked.isLoading}
          className="inline-flex shrink-0 items-center gap-1.5 rounded-md border border-glass bg-glass px-2.5 py-1 text-xs text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground disabled:opacity-60"
          title="Link a TD SYNNEX order to this ticket"
        >
          <Package className="h-3.5 w-3.5" />
          <span className="hidden lg:inline">Link order</span>
          <Plus className="h-3 w-3 lg:hidden" />
        </button>
      )}
      <TicketOrdersDialog ticketId={ticketId} open={open} onOpenChange={setOpen} orders={orders} loading={linked.isLoading} />
    </>
  );
}

const URGENCY: Record<string, number> = { REJECTED: 1, INVESTIGATION: 2, OPEN: 3, SHIPPING: 4, COMPLETED: 5 };

function pickHeadline(orders: TdOrderHeader[]): TdOrderHeader | null {
  if (orders.length === 0) return null;
  const rank = (o: TdOrderHeader) => (o.isLate ? 0 : URGENCY[(o.status ?? "").toUpperCase()] ?? 6);
  return [...orders].sort((a, b) => rank(a) - rank(b) || (a.firstShipDate ?? "9999").localeCompare(b.firstShipDate ?? "9999"))[0];
}

function chipTitle(orders: TdOrderHeader[]): string {
  return orders
    .map((o) => `${o.orderNumber}: ${o.isLate ? "late, " : ""}${statusMeta(o.status).label}${o.firstShipDate ? `, ships ${formatShipDate(o.firstShipDate)}` : ""}`)
    .join("\n") + "\nClick for details";
}

function TicketOrdersDialog({
  ticketId, open, onOpenChange, orders, loading,
}: {
  ticketId: string;
  open: boolean;
  onOpenChange: (o: boolean) => void;
  orders: TdOrderDetail[];
  loading: boolean;
}) {
  const qc = useQueryClient();
  const [expanded, setExpanded] = React.useState<string | null>(null);
  const [linking, setLinking] = React.useState(false);
  React.useEffect(() => {
    if (open) {
      setExpanded(orders.length === 1 ? orders[0].header.id : null);
      setLinking(orders.length === 0);
    }
    // Only when the dialog opens — not on every refetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ticketOrdersKey(ticketId) });
    void qc.invalidateQueries({ queryKey: ["order-management"] });
  };

  const unlink = useMutation({
    mutationFn: (orderId: string) => orderManagementApi.unlink(ticketId, orderId),
    onSuccess: () => {
      toast.success("Order unlinked");
      refresh();
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "Could not unlink the order"),
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="flex max-h-[90vh] w-[95vw] max-w-4xl flex-col">
        <DialogHeader>
          <DialogTitle>Orders on this ticket</DialogTitle>
          <DialogDescription>
            TD SYNNEX orders linked to this ticket — status, expected ship dates and deliveries, kept up to date by the sync.
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1">
          {loading ? (
            <Skeleton className="h-24 w-full" />
          ) : (
            orders.map((o) => {
              const h = o.header;
              const isOpen = expanded === h.id;
              return (
                <div key={h.id} className="sd-ticket-order glass-card overflow-hidden">
                  <div className="flex items-center gap-3 px-4 py-3">
                    <button
                      type="button"
                      onClick={() => setExpanded(isOpen ? null : h.id)}
                      className="flex min-w-0 flex-1 items-center gap-3 text-left"
                      aria-expanded={isOpen}
                    >
                      <ChevronDown className={cn("h-4 w-4 shrink-0 text-muted-foreground transition-transform", !isOpen && "-rotate-90")} />
                      <div className="min-w-0 flex-1">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="font-medium tabular-nums text-foreground">{h.orderNumber}</span>
                          <OrderStatusChip status={h.status} />
                          {h.customerOrderNumber && <span className="text-xs text-muted-foreground">PO {h.customerOrderNumber}</span>}
                        </div>
                        <div className="mt-0.5 flex flex-wrap items-center gap-x-3 text-xs text-muted-foreground">
                          <span className="truncate">{h.shipToName ?? "—"}</span>
                          <EtaLabel order={h} compact />
                        </div>
                      </div>
                    </button>
                    <Button
                      variant="ghost"
                      size="sm"
                      className="shrink-0 text-muted-foreground"
                      onClick={() => unlink.mutate(h.id)}
                      disabled={unlink.isPending}
                      title="Remove the link between this order and the ticket"
                    >
                      <Unlink className="mr-1 h-3.5 w-3.5" /> Unlink
                    </Button>
                  </div>
                  {isOpen && (
                    <div className="border-t border-glass px-4 py-4">
                      <OrderDetailPanel detail={o} compact />
                    </div>
                  )}
                </div>
              );
            })
          )}

          {linking ? (
            <OrderLinkPicker
              ticketId={ticketId}
              linkedIds={new Set(orders.map((o) => o.header.id))}
              onLinked={() => {
                refresh();
                setLinking(false);
              }}
              onCancel={orders.length > 0 ? () => setLinking(false) : undefined}
            />
          ) : (
            <Button variant="outline" size="sm" className="gap-1.5" onClick={() => setLinking(true)}>
              <Link2 className="h-3.5 w-3.5" /> Link another order
            </Button>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}

function OrderLinkPicker({
  ticketId, linkedIds, onLinked, onCancel,
}: {
  ticketId: string;
  linkedIds: Set<string>;
  onLinked: () => void;
  onCancel?: () => void;
}) {
  const [q, setQ] = React.useState("");
  const dq = useDebounced(q, 200);
  const results = useQuery({
    queryKey: ["order-management", "picker", dq],
    queryFn: () => orderManagementApi.picker(dq, 10),
    staleTime: 30_000,
  });
  const link = useMutation({
    mutationFn: (orderId: string) => orderManagementApi.link(ticketId, orderId),
    onSuccess: () => {
      toast.success("Order linked");
      onLinked();
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "Could not link the order"),
  });
  const items = (results.data?.items ?? []).filter((o) => !linkedIds.has(o.id));

  return (
    <div className="sd-ticket-order-picker space-y-2 rounded-lg border border-glass bg-glass p-3">
      <div className="flex items-center gap-2">
        <div className="relative flex-1">
          <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            autoFocus
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder="Search by order number, PO, ship-to or part number…"
            className="pl-9"
          />
        </div>
        {onCancel && (
          <Button variant="ghost" size="sm" onClick={onCancel}>
            Cancel
          </Button>
        )}
      </div>
      {results.isLoading ? (
        <Skeleton className="h-16 w-full" />
      ) : items.length === 0 ? (
        <p className="px-1 py-2 text-sm text-muted-foreground">
          {dq.trim() ? "No orders match." : "No orders to link yet — they appear here once TD SYNNEX is synced."}
        </p>
      ) : (
        <ul className="divide-y divide-glass">
          {items.map((o) => (
            <li key={o.id} className="flex items-center gap-3 py-2">
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <span className="font-medium tabular-nums text-foreground">{o.orderNumber}</span>
                  <OrderStatusChip status={o.status} />
                  {o.customerOrderNumber && <span className="text-xs text-muted-foreground">PO {o.customerOrderNumber}</span>}
                </div>
                <div className="flex flex-wrap items-center gap-x-3 text-xs text-muted-foreground">
                  <span className="truncate">{o.shipToName ?? "—"}</span>
                  <EtaLabel order={o} compact />
                  {o.linkedTicketCount > 0 && <span>on {o.linkedTicketCount} other ticket{o.linkedTicketCount === 1 ? "" : "s"}</span>}
                </div>
              </div>
              <Button size="sm" variant="outline" onClick={() => link.mutate(o.id)} disabled={link.isPending}>
                Link
              </Button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
