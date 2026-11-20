import { CalendarClock, CircleAlert } from "lucide-react";
import { cn } from "@/lib/utils";
import type { TdOrderHeader, TdOrderStatus } from "@/lib/order-management-api";
import { TONE_CLASSES, formatShipDate, statusMeta, type Tone } from "./orderFormat";

/// Small shared components of the Order Management UI.

export function OrderStatusChip({ status, className }: { status: TdOrderStatus | null | undefined; className?: string }) {
  const m = statusMeta(status);
  return (
    <span className={cn("inline-flex items-center rounded-md border px-1.5 py-px text-[11px] font-medium", TONE_CLASSES[m.tone], className)}>
      {m.label}
    </span>
  );
}

/// A split-delivery status from TD SYNNEX (e.g. WITH_VENDOR, SHIPPED,
/// SHIPPED_WITH_VENDOR). Shows TD SYNNEX's own description when it sends one
/// ("Shipped with Vendor"), else the code in sentence case.
export function SplitStatusChip({ status, detail, done }: { status: string | null; detail?: string | null; done: boolean }) {
  const tone: Tone = done ? "success" : status ? "info" : "neutral";
  const code = status ? status.replace(/_/g, " ") : null;
  const label = detail?.trim() || (code ? code.charAt(0) + code.slice(1).toLowerCase() : "—");
  return (
    <span className={cn("inline-flex items-center rounded-md border px-1.5 py-px text-[10px] font-medium", TONE_CLASSES[tone])}>
      {label}
    </span>
  );
}

/// Expected ship date cell: the earliest open date, the range when the
/// order ships in parts, and a late marker against the server's today.
export function EtaLabel({ order, compact }: { order: Pick<TdOrderHeader, "firstShipDate" | "etaDate" | "isLate" | "status">; compact?: boolean }) {
  const done = order.status === "COMPLETED" || order.status === "REJECTED";
  if (!order.firstShipDate) {
    return <span className="text-muted-foreground/70">{done ? "—" : "No date yet"}</span>;
  }
  const range = order.etaDate && order.etaDate !== order.firstShipDate;
  return (
    <span className={cn("inline-flex items-center gap-1 tabular-nums", order.isLate ? "font-medium text-rose-700 dark:text-rose-300" : "text-foreground")}>
      {order.isLate ? <CircleAlert className="h-3 w-3 shrink-0" aria-hidden /> : !compact && <CalendarClock className="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden />}
      {formatShipDate(order.firstShipDate)}
      {range && <span className="text-muted-foreground">– {formatShipDate(order.etaDate)}</span>}
      {order.isLate && <span className="sr-only">(late)</span>}
    </span>
  );
}
