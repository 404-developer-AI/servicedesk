import { useQueries } from "@tanstack/react-query";
import { ordersApi } from "@/lib/adsolut-catalogue-api";
import { formatDateTime } from "@/lib/dateFormat";
import { useServerTimeZone } from "@/hooks/useServerTime";
import type { TicketFormSummary } from "@/lib/forms-api";
import type { Ticket } from "@/lib/ticket-api";
import { Package, ClipboardList } from "lucide-react";
import { BackofficeStatusChip } from "@/pages/backoffice-forms/BackofficeStatusChip";

/* ─── Forms block ─── */

/// "Forms" strip in the Status tab: every filled form on the ticket, oldest
/// first (the order they were filled in). Rows carry `data-form-id`, so the
/// page's TicketFormHost capture-phase listener opens the form dialog — the
/// same path as the "Open form" pill in the note.
export function FormsBlock({ forms }: { forms: TicketFormSummary[] }) {
  const zone = useServerTimeZone();
  return (
    <div className="sd-forms-block space-y-2 rounded-md border border-glass-strong bg-glass px-2.5 py-2">
      <div className="flex items-center justify-between text-[10px] uppercase tracking-wider text-muted-foreground/70">
        <span>Forms</span>
        <span className="tabular-nums">{forms.length}</span>
      </div>
      <div className="space-y-1.5">
        {forms.map((f) => (
          <button
            key={f.id}
            type="button"
            data-form-id={f.id}
            className="group flex w-full items-center gap-2 rounded-md border border-glass bg-glass px-2.5 py-1.5 text-left text-xs transition-colors hover:bg-glass-hover"
          >
            <ClipboardList className="h-3.5 w-3.5 shrink-0 text-primary/80" />
            <span className="min-w-0 flex-1">
              <span className="block truncate font-medium text-foreground">{f.name}</span>
              <span className="block truncate text-[11px] text-muted-foreground">
                {f.updatedByName ?? f.createdByName ?? "—"} · {formatDateTime(f.updatedUtc, zone)}
              </span>
              {f.boStatus && (
                <BackofficeStatusChip className="mt-1"
                  item={{ status: f.boStatus, handledByName: f.boHandledByName ?? null }} />
              )}
            </span>
            {f.version > 1 && (
              <span className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium tabular-nums border border-glass bg-glass-strong text-muted-foreground/80">
                v{f.version}
              </span>
            )}
          </button>
        ))}
      </div>
    </div>
  );
}

/* ─── Orders block ─── */

/// Splits a pill label ("OR3 · Customer") back into its document-ref and
/// customer halves. A label without the separator is customer-only (see
/// orderMentionLabel).
function splitOrderLabel(label: string): [string | null, string | null] {
  const idx = label.indexOf(" · ");
  if (idx === -1) return [null, label || null];
  return [label.slice(0, idx), label.slice(idx + 3)];
}

/// "Orders" strip in the Status tab: every order tagged in the ticket via a
/// "::" pill (description or any article/note), deduped by the detail page.
/// The mirror row is fetched per order (cache key shared with
/// OrderDetailDialog) for a live status badge; until it arrives the pill's
/// own label carries the row. Rows render with `data-order-id`, so the
/// global OrderPillHost capture-phase click listener opens the same
/// OrderDetailDialog as clicking the pill in the text — no local handler.
export function OrdersBlock({ orders }: { orders: { id: string; label: string }[] }) {
  const details = useQueries({
    queries: orders.map((o) => ({
      queryKey: ["orders", "detail", o.id],
      queryFn: () => ordersApi.detail(o.id),
      staleTime: 60_000,
      retry: 1,
    })),
  });
  return (
    <div className="space-y-2 rounded-md border border-glass-strong bg-glass px-2.5 py-2">
      <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
        Orders
      </div>
      <div className="space-y-1.5">
        {orders.map((o, i) => {
          const header = details[i]?.data?.header;
          const [labelDoc, labelCustomer] = splitOrderLabel(o.label);
          const doc = header
            ? (header.kluwerRef ?? `${header.bookCode ?? ""} ${header.docNr ?? ""}`.trim())
            : labelDoc;
          const customer = header ? header.customerName : labelCustomer;
          const state = header?.stateDescription ?? header?.stateCode ?? null;
          return (
            <button
              key={o.id}
              type="button"
              data-order-id={o.id}
              className="flex w-full items-center gap-2 rounded-md border border-glass bg-glass px-2.5 py-1.5 text-left text-xs transition-colors hover:bg-glass-hover"
            >
              <Package className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
              <span className="min-w-0 flex-1">
                <span className="block truncate font-medium text-foreground">
                  {doc || "Order"}
                </span>
                {customer && (
                  <span className="block truncate text-[11px] text-muted-foreground">
                    {customer}
                  </span>
                )}
              </span>
              {state && (
                <span className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium border border-glass bg-glass-strong text-muted-foreground/80">
                  {state}
                </span>
              )}
            </button>
          );
        })}
      </div>
    </div>
  );
}

/* ─── Zammad provenance badge ─── */

/// Renders a quiet "Imported from Zammad #<number>" strip in the Status
/// tab for tickets created by the v0.0.41 migration link. Pure metadata —
/// no link out (we don't trust the Zammad install to stay reachable) and
/// no actions. The upstream number is rendered as monospace so it lines up
/// with the local ticket-number chip in the header.
export function ZammadImportBadge({ ticket }: { ticket: Ticket }) {
  if (ticket.zammadTicketId == null) return null;
  const upstreamLabel = ticket.zammadTicketNumber ?? String(ticket.zammadTicketId);
  return (
    <div className="rounded-md border border-glass-strong bg-glass px-2.5 py-1.5 text-[11px] text-muted-foreground">
      <div className="text-[9px] uppercase tracking-wider text-muted-foreground/60">
        Provenance
      </div>
      <div className="mt-0.5 flex items-baseline gap-1.5">
        <span>Imported from Zammad</span>
        <span className="font-mono text-foreground/80">#{upstreamLabel}</span>
      </div>
    </div>
  );
}
