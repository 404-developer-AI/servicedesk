import type { AdsolutOrderDetail, AdsolutSupplierOrderLine } from "@/lib/api";
import { cn } from "@/lib/utils";
import { formatDate, formatDateTime, formatDiscount, formatMoney, formatNumber } from "./orderFormat";

/// Coloured "Bestelling status" chip. The colour comes from the admin's
/// status→hex map; a missing/empty status falls back to the special NO_STATUS
/// key. When no colour is configured the chip uses a neutral glass style.
function StatusChip({
  code,
  colors,
}: {
  code: string | null | undefined;
  colors: Record<string, string>;
}) {
  const key = code && code.trim().length > 0 ? code : "NO_STATUS";
  const label = code && code.trim().length > 0 ? code : "No Status";
  const hex = colors[key];
  if (!hex) {
    return (
      <span className="inline-flex items-center rounded-full border border-glass-strong bg-glass px-2 py-0.5 text-[11px] text-muted-foreground">
        {label}
      </span>
    );
  }
  // Tint the background from the chosen colour; keep the text the solid colour
  // for contrast against the dark/light glass.
  return (
    <span
      className="inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium"
      style={{ backgroundColor: `${hex}22`, border: `1px solid ${hex}55`, color: hex }}
    >
      {label}
    </span>
  );
}

/// Order detail: header meta + the article detail lines. Reused by the Orders
/// overview (expanded row) and the ticket "::" linked-order dialog. The line
/// table carries exactly the fields the customer asked for: artikel,
/// omschrijving, bestelling-status, fact aantal/eenheid, afgeleverd, bruto,
/// korting 1, eenheidsprijs and totaal.
export function OrderDetail({ data, dense = false }: { data: AdsolutOrderDetail; dense?: boolean }) {
  const { header, lines, supplierLines, statusColors } = data;
  const currency = header.currencyIso;

  return (
    <div className={cn("space-y-4", dense && "space-y-3")}>
      <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs sm:grid-cols-4">
        <div>
          <dt className="text-muted-foreground/60">Document</dt>
          <dd className="font-mono text-foreground">
            {header.kluwerRef ?? (`${header.bookCode ?? ""} ${header.docNr ?? ""}`.trim() || "—")}
          </dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Relation</dt>
          <dd className="text-foreground">{header.customerName ?? "—"}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Order date</dt>
          <dd className="text-foreground">{formatDate(header.orderDate)}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Status</dt>
          <dd className="text-foreground">{header.stateDescription ?? header.stateCode ?? "—"}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Requested delivery</dt>
          <dd className="text-foreground">{formatDate(header.requestedDeliveryDate)}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Representative</dt>
          <dd className="text-foreground">{header.representativeName ?? "—"}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Total (excl. / incl.)</dt>
          <dd className="text-foreground">
            {formatMoney(header.totalExclVat, currency)}
            <span className="text-muted-foreground/60"> · {formatMoney(header.totalInclVat, currency)}</span>
          </dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Last synced</dt>
          <dd className="text-foreground">{formatDateTime(header.syncedUtc)}</dd>
        </div>
      </dl>

      {header.remark && (
        <p className="rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground">
          {header.remark}
        </p>
      )}

      {lines.length > 0 ? (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[56rem] text-xs">
            <thead>
              <tr className="text-left text-[11px] uppercase tracking-wider text-muted-foreground/60">
                <th className="py-1.5 pr-3 font-medium">Article</th>
                <th className="py-1.5 pr-3 font-medium">Description</th>
                <th className="py-1.5 pr-3 text-right font-medium">Qty</th>
                <th className="py-1.5 pr-3 font-medium">Unit</th>
                <th className="py-1.5 pr-3 text-right font-medium">Delivered</th>
                <th className="py-1.5 pr-3 text-right font-medium">Gross</th>
                <th className="py-1.5 pr-3 text-right font-medium">Disc. 1</th>
                <th className="py-1.5 pr-3 text-right font-medium">Unit price</th>
                <th className="py-1.5 text-right font-medium">Total</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l) => (
                <tr key={l.id} className="border-t border-glass align-top">
                  <td className="py-1.5 pr-3 font-mono text-muted-foreground">{l.productCode ?? "—"}</td>
                  <td className="max-w-[22rem] py-1.5 pr-3 text-foreground">
                    <span className="block whitespace-pre-line">{l.description || "—"}</span>
                  </td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{formatNumber(l.quantity)}</td>
                  <td className="py-1.5 pr-3 text-muted-foreground">{l.unitDescription ?? l.unitCode ?? "—"}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{formatNumber(l.delivered)}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{formatMoney(l.grossUnitPrice, currency)}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums text-muted-foreground">{formatDiscount(l.discount1)}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{formatMoney(l.unitPrice, currency)}</td>
                  <td className="py-1.5 text-right tabular-nums text-foreground">{formatMoney(l.priceExclVat, currency)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <p className="text-xs text-muted-foreground">This order has no detail lines.</p>
      )}

      <SupplierOrdersBlock lines={supplierLines} colors={statusColors} />
    </div>
  );
}

/// "Bestellingen" block — the linked supplier orders (inkooporders) for this
/// order, joined on the sales-order header. Each row carries the REAL
/// procurement status (coloured chip), supplier, date, ordered + delivered qty.
/// Empty → a single "No Status" chip so it's clear there is no procurement yet.
function SupplierOrdersBlock({
  lines,
  colors,
}: {
  lines: AdsolutSupplierOrderLine[];
  colors: Record<string, string>;
}) {
  return (
    <div>
      <h4 className="mb-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground/70">
        Bestellingen
      </h4>
      {lines.length === 0 ? (
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          <StatusChip code={null} colors={colors} />
          <span>No linked supplier order for this order.</span>
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[48rem] text-xs">
            <thead>
              <tr className="text-left text-[11px] uppercase tracking-wider text-muted-foreground/60">
                <th className="py-1.5 pr-3 font-medium">Article</th>
                <th className="py-1.5 pr-3 font-medium">Supplier</th>
                <th className="py-1.5 pr-3 font-medium">Bestelling status</th>
                <th className="py-1.5 pr-3 font-medium">Date</th>
                <th className="py-1.5 pr-3 text-right font-medium">Ordered</th>
                <th className="py-1.5 text-right font-medium">Delivered</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((s) => (
                <tr key={s.id} className="border-t border-glass align-top">
                  <td className="py-1.5 pr-3">
                    <span className="font-mono text-muted-foreground">{s.productCode ?? "—"}</span>
                    {s.description && (
                      <span className="block max-w-[20rem] truncate text-muted-foreground/70">{s.description}</span>
                    )}
                  </td>
                  <td className="py-1.5 pr-3 text-foreground">
                    {s.supplierName ?? "—"}
                    {s.blDocNr != null && (
                      <span className="ml-1.5 font-mono text-[10px] text-muted-foreground/60">
                        {s.blBookCode ?? "BL"} {s.blDocNr}
                      </span>
                    )}
                  </td>
                  <td className="py-1.5 pr-3">
                    <StatusChip code={s.statusCode} colors={colors} />
                  </td>
                  <td className="whitespace-nowrap py-1.5 pr-3">{formatDate(s.supplierOrderDate)}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{formatNumber(s.quantity)}</td>
                  <td className="py-1.5 text-right tabular-nums text-foreground">{formatNumber(s.delivered)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
