import * as React from "react";
import { Check, Copy, FileDown } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { useTicketReferencePrefix } from "@/hooks/useTicketReferencePrefix";
import { formatTicketRef } from "@/lib/ticketRef";
import { ticketApi } from "@/lib/ticket-api";

export function LoadingSkeleton() {
  return (
    <div className="flex gap-6 pt-3 h-[calc(100vh-0.75rem)] overflow-hidden">
      <div className="flex-1 space-y-4">
        <Skeleton className="h-8 w-2/3" />
        <Skeleton className="h-4 w-1/4" />
        <div className="glass-card p-6 space-y-3">
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-5/6" />
          <Skeleton className="h-4 w-4/6" />
        </div>
        <Skeleton className="h-6 w-32" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
      <div className="w-[320px] shrink-0 space-y-4">
        <Skeleton className="h-[480px] w-full rounded-(--radius)" />
      </div>
    </div>
  );
}

/* ─── Click-to-copy ticket number ─── */

export function TicketNumber({ number }: { number: number }) {
  const [copied, setCopied] = React.useState(false);
  const refPrefix = useTicketReferencePrefix();

  const handleCopy = async () => {
    // Copy the full reference ("Ticket#1234"). The backend parsers for global
    // search, the ticket picker, timesheet links and inbound mail all strip
    // the prefix again, so pasting this form resolves straight back to the
    // ticket without anyone having to delete "Ticket#" by hand.
    await navigator.clipboard.writeText(formatTicketRef(number, refPrefix));
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  return (
    <button
      type="button"
      onClick={handleCopy}
      title="Click to copy"
      className="group flex items-center gap-1 text-xs font-mono font-medium px-2 py-0.5 rounded border border-glass bg-glass text-muted-foreground hover:bg-glass-hover transition-colors"
    >
      #{number}
      {copied ? (
        <Check className="h-3 w-3 text-green-400" />
      ) : (
        <Copy className="h-3 w-3 opacity-0 group-hover:opacity-60 transition-opacity" />
      )}
    </button>
  );
}

/* ─── Export PDF button ─── */

export function ExportPdfButton({ ticketId }: { ticketId: string }) {
  const [open, setOpen] = React.useState(false);
  const [includeInternal, setIncludeInternal] = React.useState(false);
  const ref = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  const handleExport = () => {
    const url = ticketApi.exportPdf(ticketId, !includeInternal);
    window.open(url, "_blank");
    setOpen(false);
  };

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen(!open)}
        className="flex items-center gap-1.5 px-2.5 py-1 text-xs font-medium rounded-md border border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground transition-colors"
        title="Export as PDF"
      >
        <FileDown className="h-3.5 w-3.5" />
        PDF
      </button>
      {open && (
        <div className="absolute right-0 top-full mt-1.5 z-50 w-56 rounded-lg border border-glass bg-background/95 backdrop-blur-xl p-3 shadow-[0_8px_30px_-12px_rgba(0,0,0,0.6)]">
          <label className="flex items-center gap-2 text-xs text-foreground/80 cursor-pointer mb-3">
            <input
              type="checkbox"
              checked={includeInternal}
              onChange={(e) => setIncludeInternal(e.target.checked)}
              className="rounded border-glass-strong bg-glass-strong text-primary focus:ring-primary/50"
            />
            Include internal events
          </label>
          <button
            type="button"
            onClick={handleExport}
            className="w-full flex items-center justify-center gap-2 px-3 py-1.5 text-xs font-medium rounded-md bg-primary text-white hover:bg-primary/90 transition-colors"
          >
            <FileDown className="h-3.5 w-3.5" />
            Export PDF
          </button>
        </div>
      )}
    </div>
  );
}
