import type { AdsolutContractDetail } from "@/lib/api";

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

function formatNumber(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return Number(value).toLocaleString("nl-BE", { maximumFractionDigits: 4 });
}

/// Contract detail: header metadata + article lines. Shown in the expanded row
/// of the Contracts overview.
export function ContractDetail({ data }: { data: AdsolutContractDetail }) {
  const { header, lines } = data;

  return (
    <div className="space-y-4">
      <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs sm:grid-cols-4">
        <div>
          <dt className="text-muted-foreground/60">Doc nr</dt>
          <dd className="font-mono text-foreground">{header.docNr ?? "—"}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Relation</dt>
          <dd className="text-foreground">{header.companyName ?? header.customerName ?? "—"}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Status</dt>
          <dd className="text-foreground">{header.stateDescription ?? header.stateCode ?? "—"}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Doc date</dt>
          <dd className="text-foreground">{formatDate(header.docDate)}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Start date</dt>
          <dd className="text-foreground">{formatDate(header.startDate)}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">End date</dt>
          <dd className="text-foreground">{formatDate(header.endDate)}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Stop date</dt>
          <dd className="text-foreground">{formatDate(header.stopDate)}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Periodicity</dt>
          <dd className="text-foreground">
            {header.periodicityLabel ?? header.periodicityCode ?? "—"}
          </dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Invoicing</dt>
          <dd className="text-foreground">
            {header.invoicingPeriodicityLabel ?? header.invoicingPeriodicityCode ?? "—"}
          </dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Terms</dt>
          <dd className="text-foreground">{header.numberOfTerms ?? "—"}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Total (excl. / incl.)</dt>
          <dd className="text-foreground">
            {formatMoney(header.totalExclVat)}
            <span className="text-muted-foreground/60"> · {formatMoney(header.totalInclVat)}</span>
          </dd>
        </div>
        <div>
          <dt className="text-muted-foreground/60">Last synced</dt>
          <dd className="text-foreground">{formatDateTime(header.syncedUtc)}</dd>
        </div>
      </dl>

      {header.memo && (
        <p className="rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground">
          {header.memo}
        </p>
      )}

      {lines.length > 0 ? (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[48rem] text-xs">
            <thead>
              <tr className="text-left text-[11px] uppercase tracking-wider text-muted-foreground/60">
                <th className="py-1.5 pr-3 font-medium">Line</th>
                <th className="py-1.5 pr-3 font-medium">Name</th>
                <th className="py-1.5 pr-3 font-medium">Description</th>
                <th className="py-1.5 pr-3 text-right font-medium">Quantity</th>
                <th className="py-1.5 pr-3 text-right font-medium">Gross unit price</th>
                <th className="py-1.5 text-right font-medium">Unit price</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l) => (
                <tr key={l.id} className="border-t border-glass align-top">
                  <td className="py-1.5 pr-3 font-mono text-muted-foreground">{l.lineNr ?? "—"}</td>
                  <td className="py-1.5 pr-3 text-foreground">{l.name || "—"}</td>
                  <td className="max-w-[22rem] py-1.5 pr-3 text-muted-foreground">
                    <span className="block whitespace-pre-line">{l.description || "—"}</span>
                  </td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{formatNumber(l.quantity)}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{formatMoney(l.grossUnitPrice)}</td>
                  <td className="py-1.5 text-right tabular-nums">{formatMoney(l.unitPrice)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <p className="text-xs text-muted-foreground">This contract has no article lines.</p>
      )}
    </div>
  );
}
