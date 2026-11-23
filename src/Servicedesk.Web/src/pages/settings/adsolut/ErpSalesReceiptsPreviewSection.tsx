import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { Copy, Search } from "lucide-react";
import { adsolutApi, type AdsolutErpProbeResponse } from "@/lib/adsolut-api";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { copyDebugBody } from "@/pages/settings/adsolut/shared";
import { errorMessage } from "@/lib/http";

export function ErpSalesReceiptsPreviewSection() {
  // ERP sales-receipts preview card — fans out a fixed set of read-only
  // probes so an empty-body 500 can be localised (param vs whole data path).
  const [erpResult, setErpResult] = useState<AdsolutErpProbeResponse | null>(null);

  const erpPreview = useMutation({
    mutationFn: () => adsolutApi.debugErpSalesReceipts(),
    onSuccess: (r) => setErpResult(r),
    onError: (err) => {
      setErpResult(null);
      toast.error(
        errorMessage(err, "Preview failed"),
      );
    },
  });

  // Per-attempt helpers (the ERP probe returns several attempts at once).
  const erpFormatBody = (body: string) => {
    if (!body) return "";
    try {
      return JSON.stringify(JSON.parse(body), null, 2);
    } catch {
      return body;
    }
  };
  const httpStatusTone = (s: number) => {
    if (s === 0) return "border-rose-400/40 bg-rose-500/10 text-rose-300";
    if (s >= 200 && s < 300) return "border-emerald-400/30 bg-emerald-500/10 text-emerald-300";
    if (s >= 400 && s < 500) return "border-amber-400/30 bg-amber-500/8 text-amber-200";
    return "border-rose-400/40 bg-rose-500/10 text-rose-300";
  };

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          ERP sales receipts · preview
        </h2>
        <p className="text-xs text-muted-foreground">
          Probes the ERP sales-receipts (verkoopbonnen) endpoint with a small set
          of read-only calls and shows each raw response — used to confirm the real
          status codes + field shape, and to localise a Wolters Kluwer error (one
          probe works → it's a query param; all fail → the ERP data path / module
          for this dossier). Requires the{" "}
          <span className="font-mono">WK.BE.ERP.Read</span> scope (ticked above and
          reconnected). Each call is audited as{" "}
          <span className="font-mono">debug.erp_sales_receipts</span>.
        </p>
      </header>

      <div className="flex flex-wrap items-center gap-3">
        <Button size="sm" onClick={() => erpPreview.mutate()} disabled={erpPreview.isPending}>
          <Search className="mr-1.5 h-3.5 w-3.5" />
          {erpPreview.isPending ? "Probing…" : "Run ERP probes"}
        </Button>
      </div>

      {erpResult && (
        <div className="mt-4 space-y-4">
          {erpResult.attempts.map((a) => (
            <div key={a.label} className="space-y-2">
              <div className="flex flex-wrap items-center gap-2 text-xs">
                <span className="font-medium text-foreground">{a.label}</span>
                <span
                  className={cn(
                    "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5",
                    httpStatusTone(a.status),
                  )}
                >
                  {a.status === 0 ? "no response" : `HTTP ${a.status}`}
                </span>
                {a.upstreamErrorCode && (
                  <span className="rounded border border-glass-strong bg-glass px-2 py-0.5 font-mono text-[11px] text-muted-foreground">
                    {a.upstreamErrorCode}
                  </span>
                )}
                <code className="truncate text-[11px] text-muted-foreground/80">
                  GET {a.requestUrl}
                </code>
              </div>
              <div className="relative">
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => copyDebugBody(erpFormatBody(a.body))}
                  aria-label="Copy response body"
                  className="absolute right-2 top-2 h-7 w-7 p-0"
                >
                  <Copy className="h-3.5 w-3.5" />
                </Button>
                <pre className="max-h-80 overflow-auto rounded-md border border-glass-strong bg-black/30 p-3 pr-10 font-mono text-[11px] leading-relaxed text-foreground/90">
                  {erpFormatBody(a.body) || "(empty body)"}
                </pre>
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
