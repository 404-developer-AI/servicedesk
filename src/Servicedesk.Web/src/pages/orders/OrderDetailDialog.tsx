import { useQuery } from "@tanstack/react-query";
import { ordersApi } from "@/lib/api";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { OrderDetail } from "./OrderDetail";

/// Controlled dialog showing a single Adsolut order with its detail lines.
/// Opened from a "::" order pill (in the ticket editor or a rendered note) via
/// the global OrderPillHost, and reusable anywhere an order id is in hand.
export function OrderDetailDialog({
  orderId,
  open,
  onOpenChange,
}: {
  orderId: string | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const detail = useQuery({
    queryKey: ["orders", "detail", orderId],
    queryFn: () => ordersApi.detail(orderId!),
    enabled: open && !!orderId,
  });

  const header = detail.data?.header;
  const title = header
    ? `${header.kluwerRef ?? `${header.bookCode ?? ""} ${header.docNr ?? ""}`.trim()} · ${header.customerName ?? "—"}`
    : "Order";

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* Responsive: scale to the viewport (95vw) up to a comfortable cap, and
          cap height at 90vh so the body scrolls inside instead of the dialog
          overflowing the screen. */}
      <DialogContent className="flex max-h-[90vh] w-[95vw] max-w-6xl flex-col">
        <DialogHeader>
          <DialogTitle className="truncate">{title}</DialogTitle>
          <DialogDescription>Adsolut order detail with its lines.</DialogDescription>
        </DialogHeader>
        {detail.isLoading ? (
          <Skeleton className="h-40 w-full" />
        ) : detail.isError || !detail.data ? (
          <p className="py-6 text-sm text-rose-300">Could not load this order.</p>
        ) : (
          <div className="min-h-0 flex-1 overflow-y-auto pr-1">
            <OrderDetail data={detail.data} dense />
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
