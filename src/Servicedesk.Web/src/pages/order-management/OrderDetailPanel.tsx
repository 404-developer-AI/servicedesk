import type { ReactNode } from "react";
import { Link } from "@tanstack/react-router";
import { FileText, History, MapPin, Truck } from "lucide-react";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { formatMoney, formatTimeAgo } from "@/lib/format";
import { cn } from "@/lib/utils";
import type { TdOrderDetail, TdOrderLinkedTicket } from "@/lib/order-management-api";
import { EtaLabel, OrderStatusChip, SplitStatusChip } from "./orderUi";
import { formatQty, formatShipDate } from "./orderFormat";

/// Everything known about one TD SYNNEX order: key facts, the lines with
/// their split deliveries (quantity · ship date · status · delivery notes ·
/// invoices), ship-to, totals and the change history. Used by the dashboard
/// dialog and the ticket's order popup.
export function OrderDetailPanel({
  detail,
  linkedTickets,
  hiddenLinkedTicketCount = 0,
  compact = false,
}: {
  detail: TdOrderDetail;
  linkedTickets?: TdOrderLinkedTicket[];
  hiddenLinkedTicketCount?: number;
  compact?: boolean;
}) {
  const zone = useServerTimeZone();
  const h = detail.header;
  const currency = h.currency ?? detail.payment?.currency ?? "EUR";

  const facts: { label: string; value: ReactNode }[] = [
    { label: "Status", value: <span className="inline-flex items-center gap-1.5"><OrderStatusChip status={h.status} />{h.statusDetail && h.statusDetail.toUpperCase() !== (h.status ?? "") && <span className="text-xs text-muted-foreground">{h.statusDetail}</span>}</span> },
    { label: "Expected ship date", value: <EtaLabel order={h} /> },
    { label: "Customer PO", value: h.customerOrderNumber ?? "—" },
    { label: "End-user reference", value: h.endUserOrderNumber ?? "—" },
    { label: "Ordered", value: h.createdUtc ? formatDateTime(h.createdUtc, zone) : "—" },
    { label: "Ordered by", value: h.createdBy ?? "—" },
    { label: "Order type", value: h.orderType ?? "—" },
    { label: "Total (net)", value: <span className="tabular-nums">{formatMoney(h.totalNet, currency)}</span> },
  ];

  return (
    <div className="sd-om-detail space-y-5">
      <dl className={cn("grid gap-x-6 gap-y-3", compact ? "grid-cols-2" : "grid-cols-2 lg:grid-cols-4")}>
        {facts.map((f) => (
          <div key={f.label} className="min-w-0">
            <dt className="text-[11px] text-muted-foreground">{f.label}</dt>
            <dd className="truncate text-sm text-foreground">{f.value}</dd>
          </div>
        ))}
      </dl>

      <section className="space-y-2">
        <h3 className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
          <Truck className="h-3.5 w-3.5" /> Lines and deliveries
        </h3>
        {detail.lines.length === 0 ? (
          <p className="text-sm text-muted-foreground">No line details received for this order.</p>
        ) : (
          <div className="overflow-x-auto rounded-md border border-glass">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-glass text-left text-[11px] text-muted-foreground">
                  <th className="px-3 py-2 font-medium">Product</th>
                  <th className="px-3 py-2 text-right font-medium">Qty</th>
                  {!compact && <th className="px-3 py-2 text-right font-medium">Net</th>}
                  <th className="px-3 py-2 font-medium">Deliveries</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-glass">
                {detail.lines.map((l, i) => (
                  <tr key={`${l.lineId ?? i}`} className="align-top">
                    <td className="max-w-md px-3 py-2">
                      <div className="text-foreground">{l.description ?? "—"}</div>
                      <div className="mt-0.5 flex flex-wrap gap-x-2 text-[11px] text-muted-foreground">
                        {l.manufacturer && <span>{l.manufacturer}</span>}
                        {l.manufacturerPartNumber && <span>MPN {l.manufacturerPartNumber}</span>}
                        {l.tdPartNumber && <span>TD {l.tdPartNumber}</span>}
                        {l.lineId && <span>Line {l.lineId}</span>}
                        {l.isEol && <span className="font-medium text-amber-700 dark:text-amber-300">End of life</span>}
                      </div>
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">{formatQty(l.quantity)}</td>
                    {!compact && (
                      <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums text-muted-foreground">
                        {formatMoney(l.totalNetPrice, currency)}
                      </td>
                    )}
                    <td className="px-3 py-2">
                      {l.splits.length === 0 ? (
                        <span className="text-xs text-muted-foreground">—</span>
                      ) : (
                        <ul className="space-y-1">
                          {l.splits.map((s, j) => (
                            <li key={j} className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-xs">
                              <span className="tabular-nums text-foreground">{formatQty(s.quantity)}×</span>
                              <span className={cn("tabular-nums", s.isDone ? "text-muted-foreground" : "text-foreground")}>
                                {s.shipDate ? formatShipDate(s.shipDate) : s.shipDateRaw ?? "no date"}
                              </span>
                              <SplitStatusChip status={s.status} done={s.isDone} />
                              {s.shipDateDetails && <span className="text-muted-foreground">{s.shipDateDetails}</span>}
                              {s.deliveryNotes.length > 0 && (
                                <span className="text-muted-foreground">DN {s.deliveryNotes.join(", ")}</span>
                              )}
                              {s.invoices.length > 0 && (
                                <span className="inline-flex items-center gap-0.5 text-muted-foreground">
                                  <FileText className="h-3 w-3" aria-hidden /> {s.invoices.join(", ")}
                                </span>
                              )}
                            </li>
                          ))}
                        </ul>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <div className={cn("grid gap-5", compact ? "grid-cols-1" : "md:grid-cols-2")}>
        {detail.shipTo && (
          <section className="space-y-1.5">
            <h3 className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
              <MapPin className="h-3.5 w-3.5" /> Ship to
            </h3>
            <address className="text-sm not-italic leading-relaxed text-foreground">
              {[detail.shipTo.companyName, detail.shipTo.contactName, detail.shipTo.line1, detail.shipTo.line2, detail.shipTo.line3,
                [detail.shipTo.zip, detail.shipTo.city].filter(Boolean).join(" "), detail.shipTo.country]
                .filter((x) => !!x && String(x).trim().length > 0)
                .map((x, i) => <div key={i}>{x}</div>)}
              {(detail.shipTo.contactEmail || detail.shipTo.phone) && (
                <div className="text-xs text-muted-foreground">
                  {[detail.shipTo.contactEmail, detail.shipTo.phone].filter(Boolean).join(" · ")}
                </div>
              )}
            </address>
          </section>
        )}

        {!compact && detail.payment && (
          <section className="space-y-1.5">
            <h3 className="text-xs font-medium text-muted-foreground">Totals</h3>
            <dl className="grid max-w-xs grid-cols-2 gap-y-1 text-sm">
              {([
                ["Subtotal", detail.payment.subtotal],
                ["Freight", detail.payment.freight],
                ["Other fees", detail.payment.otherFees],
                ["Tax", detail.payment.tax],
                ["Total", detail.payment.total],
              ] as const)
                .filter(([, v]) => v !== null && v !== undefined)
                .map(([k, v]) => (
                  <div key={k} className="contents">
                    <dt className="text-muted-foreground">{k}</dt>
                    <dd className="text-right tabular-nums text-foreground">{formatMoney(v, currency)}</dd>
                  </div>
                ))}
            </dl>
            {detail.payment.paymentTerms && <p className="text-xs text-muted-foreground">{detail.payment.paymentTerms}</p>}
          </section>
        )}
      </div>

      {linkedTickets && (
        <section className="space-y-1.5">
          <h3 className="text-xs font-medium text-muted-foreground">Linked tickets</h3>
          {linkedTickets.length === 0 && hiddenLinkedTicketCount === 0 ? (
            <p className="text-sm text-muted-foreground">Not linked to a ticket yet. Link it from the ticket's order button.</p>
          ) : (
            <ul className="space-y-1">
              {linkedTickets.map((t) => (
                <li key={t.id} className="flex items-center gap-2 text-sm">
                  <span className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: t.statusColor }} aria-hidden />
                  <Link to="/tickets/$ticketId" params={{ ticketId: t.id }} className="truncate text-foreground hover:underline">
                    <span className="tabular-nums text-muted-foreground">#{t.number}</span> {t.subject}
                  </Link>
                  <span className="shrink-0 text-xs text-muted-foreground">{t.statusName}</span>
                </li>
              ))}
              {hiddenLinkedTicketCount > 0 && (
                <li className="text-xs text-muted-foreground">
                  + {hiddenLinkedTicketCount} ticket{hiddenLinkedTicketCount === 1 ? "" : "s"} in queues you cannot open
                </li>
              )}
            </ul>
          )}
        </section>
      )}

      <section className="space-y-1.5">
        <h3 className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
          <History className="h-3.5 w-3.5" /> Changes
        </h3>
        {detail.changes.length === 0 ? (
          <p className="text-sm text-muted-foreground">No changes since the order was first synced.</p>
        ) : (
          <ol className="space-y-2 border-l border-glass-strong pl-3">
            {detail.changes.map((c) => (
              <li key={c.id} className="text-sm">
                <div className="text-[11px] text-muted-foreground" title={formatDateTime(c.detectedUtc, zone)}>
                  {formatTimeAgo(c.detectedUtc, { dateAfterDays: 7 })}
                </div>
                {c.items.length > 0 ? (
                  <ul className="text-foreground">
                    {c.items.map((it, i) => <li key={i}>{it.text}</li>)}
                  </ul>
                ) : (
                  <div className="text-foreground">{c.summary}</div>
                )}
              </li>
            ))}
          </ol>
        )}
      </section>

      <p className="text-[11px] text-muted-foreground/70">
        Last synced {formatTimeAgo(h.lastSyncedUtc)} · order {h.orderNumber}
      </p>
    </div>
  );
}
