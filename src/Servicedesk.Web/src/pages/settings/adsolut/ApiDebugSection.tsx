import { useMemo, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { Copy, Search } from "lucide-react";
import {
  ApiError,
  adsolutApi,
  type AdsolutDebugKind,
  type AdsolutDebugLookupResponse,
} from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";
import { copyDebugBody } from "@/pages/settings/adsolut/shared";

export function ApiDebugSection() {
  const [debugKind, setDebugKind] = useState<AdsolutDebugKind>("customer");
  const [debugCode, setDebugCode] = useState<string>("");
  const [debugResult, setDebugResult] = useState<AdsolutDebugLookupResponse | null>(null);

  const debugLookup = useMutation({
    mutationFn: () => adsolutApi.debugLookup(debugKind, debugCode.trim()),
    onSuccess: (r) => setDebugResult(r),
    onError: (err) => {
      setDebugResult(null);
      toast.error(
        err instanceof ApiError ? `Lookup failed (${err.status})` : "Lookup failed",
      );
    },
  });

  // Pretty-print the upstream body when it parses as JSON, fall back to the
  // raw string verbatim. The status pill is independent of parse success —
  // a 200 with an HTML error page should still show as 200 with the HTML.
  const debugFormattedBody = useMemo(() => {
    if (!debugResult?.body) return "";
    try {
      return JSON.stringify(JSON.parse(debugResult.body), null, 2);
    } catch {
      return debugResult.body;
    }
  }, [debugResult?.body]);

  const debugStatusTone = (() => {
    if (!debugResult) return "";
    const s = debugResult.status;
    if (s === 0) return "border-rose-400/40 bg-rose-500/10 text-rose-300";
    if (s >= 200 && s < 300) return "border-emerald-400/30 bg-emerald-500/10 text-emerald-300";
    if (s >= 400 && s < 500) return "border-amber-400/30 bg-amber-500/8 text-amber-200";
    return "border-rose-400/40 bg-rose-500/10 text-rose-300";
  })();

  const debugCanLookup =
    debugCode.trim().length > 0 && debugCode.trim().length <= 32 && !debugLookup.isPending;

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          API debug
        </h2>
        <p className="text-xs text-muted-foreground">
          Probe Adsolut's customers or suppliers list-endpoint by relation code and
          inspect the raw JSON response, including fields the sync worker does not
          currently persist. Each call lands in the audit log as{" "}
          <span className="font-mono">debug.lookup</span>.
        </p>
      </header>

      <div className="flex flex-wrap items-end gap-2">
        <div
          role="tablist"
          aria-label="Lookup kind"
          className="inline-flex rounded-md border border-glass-strong bg-glass p-0.5 text-xs"
        >
          {(["customer", "supplier"] as const).map((k) => (
            <button
              key={k}
              type="button"
              role="tab"
              aria-selected={debugKind === k}
              onClick={() => setDebugKind(k)}
              className={cn(
                "rounded px-3 py-1 capitalize transition-colors",
                debugKind === k
                  ? "bg-glass-strong text-foreground shadow-xs"
                  : "text-muted-foreground hover:text-foreground",
              )}
            >
              {k}
            </button>
          ))}
        </div>
        <Input
          value={debugCode}
          onChange={(e) => setDebugCode(e.target.value)}
          placeholder="Relation code (e.g. 998)"
          maxLength={32}
          className="max-w-xs"
          onKeyDown={(e) => {
            if (e.key === "Enter" && debugCanLookup) {
              e.preventDefault();
              debugLookup.mutate();
            }
          }}
        />
        <Button
          size="sm"
          onClick={() => debugLookup.mutate()}
          disabled={!debugCanLookup}
        >
          <Search className="mr-1.5 h-3.5 w-3.5" />
          {debugLookup.isPending ? "Looking up…" : "Lookup"}
        </Button>
      </div>

      {debugResult && (
        <div className="mt-4 space-y-2">
          <div className="flex flex-wrap items-center gap-2 text-xs">
            <span
              className={cn(
                "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5",
                debugStatusTone,
              )}
            >
              {debugResult.status === 0 ? "no response" : `HTTP ${debugResult.status}`}
            </span>
            {debugResult.upstreamErrorCode && (
              <span className="rounded border border-glass-strong bg-glass px-2 py-0.5 font-mono text-[11px] text-muted-foreground">
                {debugResult.upstreamErrorCode}
              </span>
            )}
            <code className="truncate text-[11px] text-muted-foreground/80">
              GET {debugResult.requestUrl}
            </code>
          </div>
          <div className="relative">
            <Button
              size="sm"
              variant="ghost"
              onClick={() => copyDebugBody(debugFormattedBody)}
              aria-label="Copy response body"
              className="absolute right-2 top-2 h-7 w-7 p-0"
            >
              <Copy className="h-3.5 w-3.5" />
            </Button>
            <pre className="max-h-112 overflow-auto rounded-md border border-glass-strong bg-black/30 p-3 pr-10 font-mono text-[11px] leading-relaxed text-foreground/90">
              {debugFormattedBody || "(empty body)"}
            </pre>
          </div>
        </div>
      )}
    </section>
  );
}
