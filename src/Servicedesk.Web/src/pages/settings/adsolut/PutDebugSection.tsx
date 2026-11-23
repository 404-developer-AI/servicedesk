import { useMemo, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { Copy, Send, Wand2 } from "lucide-react";
import {
  adsolutApi,
  type AdsolutDebugPutPreview,
  type AdsolutDebugPutResponse,
} from "@/lib/adsolut-api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";
import { copyDebugBody } from "@/pages/settings/adsolut/shared";
import { errorMessage } from "@/lib/http";

export function PutDebugSection() {
  // PUT-debug card — separate from the lookup card so an in-progress edit
  // is not lost when the admin runs another lookup.
  const [putCustomerId, setPutCustomerId] = useState<string>("");
  const [putPreview, setPutPreview] = useState<AdsolutDebugPutPreview | null>(null);
  const [putBodyDraft, setPutBodyDraft] = useState<string>("");
  const [putResult, setPutResult] = useState<AdsolutDebugPutResponse | null>(null);

  const buildPutPreview = useMutation({
    mutationFn: () => adsolutApi.debugPutPreview(putCustomerId.trim()),
    onSuccess: (r) => {
      setPutPreview(r);
      // Pre-fill the editor only on a successful build. Leaves any in-
      // flight edit intact when the GET fails (so a 404 doesn't wipe
      // a body the admin was already crafting from a previous customer).
      if (r.putBody) {
        setPutBodyDraft(r.putBody);
      }
      setPutResult(null);
    },
    onError: (err) => {
      toast.error(
        errorMessage(err, "Preview failed"),
      );
    },
  });

  const sendCustomerPut = useMutation({
    mutationFn: () => adsolutApi.debugCustomerPut(putCustomerId.trim(), putBodyDraft),
    onSuccess: (r) => {
      setPutResult(r);
      if (r.status >= 200 && r.status < 300) {
        toast.success(`PUT succeeded — HTTP ${r.status}`);
      } else if (r.status === 0) {
        toast.error(`PUT failed — no response`);
      } else {
        toast.error(`PUT returned HTTP ${r.status}`);
      }
    },
    onError: (err) => {
      setPutResult(null);
      toast.error(
        errorMessage(err, "PUT failed"),
      );
    },
  });

  const isUuid = (v: string) =>
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v.trim());

  const putCanBuild = isUuid(putCustomerId) && !buildPutPreview.isPending;
  const putBodyParses = useMemo(() => {
    if (!putBodyDraft.trim()) return false;
    try {
      JSON.parse(putBodyDraft);
      return true;
    } catch {
      return false;
    }
  }, [putBodyDraft]);
  const putCanSend =
    isUuid(putCustomerId) && putBodyDraft.trim().length > 0 && putBodyParses && !sendCustomerPut.isPending;

  const putGetStatusTone = (() => {
    if (!putPreview) return "";
    const s = putPreview.getStatus;
    if (s === 0) return "border-rose-400/40 bg-rose-500/10 text-rose-300";
    if (s >= 200 && s < 300) return "border-emerald-400/30 bg-emerald-500/10 text-emerald-300";
    if (s >= 400 && s < 500) return "border-amber-400/30 bg-amber-500/8 text-amber-200";
    return "border-rose-400/40 bg-rose-500/10 text-rose-300";
  })();
  const putResultStatusTone = (() => {
    if (!putResult) return "";
    const s = putResult.status;
    if (s === 0) return "border-rose-400/40 bg-rose-500/10 text-rose-300";
    if (s >= 200 && s < 300) return "border-emerald-400/30 bg-emerald-500/10 text-emerald-300";
    if (s >= 400 && s < 500) return "border-amber-400/30 bg-amber-500/8 text-amber-200";
    return "border-rose-400/40 bg-rose-500/10 text-rose-300";
  })();
  const putGetFormatted = useMemo(() => {
    if (!putPreview?.getBody) return "";
    try {
      return JSON.stringify(JSON.parse(putPreview.getBody), null, 2);
    } catch {
      return putPreview.getBody;
    }
  }, [putPreview?.getBody]);
  const putResultFormatted = useMemo(() => {
    if (!putResult?.body) return "";
    try {
      return JSON.stringify(JSON.parse(putResult.body), null, 2);
    } catch {
      return putResult.body;
    }
  }, [putResult?.body]);

  const resetPutBodyToComputed = () => {
    if (putPreview?.putBody) {
      setPutBodyDraft(putPreview.putBody);
    }
  };

  const formatPutBodyDraft = () => {
    try {
      setPutBodyDraft(JSON.stringify(JSON.parse(putBodyDraft), null, 2));
    } catch {
      toast.error("Body is not valid JSON — cannot format");
    }
  };

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          PUT debug · customers
        </h2>
        <p className="text-xs text-muted-foreground">
          Build the PUT body our pusher would send for a single customer, edit it
          in place if needed, then send the PUT to Adsolut. Use the Lookup card
          above to find the customer's <span className="font-mono">id</span>{" "}
          (UUID) — paste that here. The body is sent verbatim as written below;
          this bypasses the SD-managed overlay so the admin owns the bytes.
        </p>
      </header>

      <div className="flex flex-wrap items-end gap-2">
        <Input
          value={putCustomerId}
          onChange={(e) => setPutCustomerId(e.target.value)}
          placeholder="Customer id (UUID, e.g. c04146fc-48ce-4f59-928a-1aad4d3bbbf9)"
          className="max-w-md font-mono text-[12px]"
          onKeyDown={(e) => {
            if (e.key === "Enter" && putCanBuild) {
              e.preventDefault();
              buildPutPreview.mutate();
            }
          }}
        />
        <Button
          size="sm"
          onClick={() => buildPutPreview.mutate()}
          disabled={!putCanBuild}
        >
          <Wand2 className="mr-1.5 h-3.5 w-3.5" />
          {buildPutPreview.isPending ? "Building…" : "Build PUT body"}
        </Button>
      </div>

      {putCustomerId.trim().length > 0 && !isUuid(putCustomerId) && (
        <p className="mt-2 text-[11px] text-amber-300">
          Customer id must be a UUID.
        </p>
      )}

      {putPreview && (
        <div className="mt-4 space-y-4">
          {/* GET status + raw upstream body */}
          <div className="space-y-2">
            <div className="flex flex-wrap items-center gap-2 text-xs">
              <span className="text-muted-foreground/70">Upstream GET:</span>
              <span
                className={cn(
                  "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5",
                  putGetStatusTone,
                )}
              >
                {putPreview.getStatus === 0 ? "no response" : `HTTP ${putPreview.getStatus}`}
              </span>
              {putPreview.upstreamErrorCode && (
                <span className="rounded border border-glass-strong bg-glass px-2 py-0.5 font-mono text-[11px] text-muted-foreground">
                  {putPreview.upstreamErrorCode}
                </span>
              )}
              <code className="truncate text-[11px] text-muted-foreground/80">
                GET {putPreview.getRequestUrl}
              </code>
            </div>
            <div className="relative">
              <Button
                size="sm"
                variant="ghost"
                onClick={() => copyDebugBody(putGetFormatted)}
                aria-label="Copy GET response body"
                className="absolute right-2 top-2 h-7 w-7 p-0"
              >
                <Copy className="h-3.5 w-3.5" />
              </Button>
              <pre className="max-h-80 overflow-auto rounded-md border border-glass-strong bg-black/30 p-3 pr-10 font-mono text-[11px] leading-relaxed text-foreground/90">
                {putGetFormatted || "(empty body)"}
              </pre>
            </div>
          </div>

          {/* Computed PUT body editor */}
          {putPreview.putBody !== null ? (
            <div className="space-y-2">
              <div className="flex flex-wrap items-center gap-2 text-xs">
                <span className="text-muted-foreground/70">Body to PUT:</span>
                <code className="truncate text-[11px] text-muted-foreground/80">
                  PUT {putPreview.putUrl}
                </code>
                <span className="ml-auto flex items-center gap-2">
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={formatPutBodyDraft}
                    disabled={!putBodyDraft.trim()}
                    className="h-7 text-[11px]"
                  >
                    Format
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={resetPutBodyToComputed}
                    disabled={!putPreview?.putBody || putBodyDraft === putPreview.putBody}
                    className="h-7 text-[11px]"
                  >
                    Reset
                  </Button>
                  {!putBodyParses && putBodyDraft.trim().length > 0 && (
                    <span className="text-[11px] text-amber-300">Invalid JSON</span>
                  )}
                </span>
              </div>
              <textarea
                value={putBodyDraft}
                onChange={(e) => setPutBodyDraft(e.target.value)}
                spellCheck={false}
                className={cn(
                  "block w-full resize-y rounded-md border bg-black/30 p-3 font-mono text-[11px] leading-relaxed text-foreground/90 focus:outline-hidden focus:ring-1 focus:ring-primary/40",
                  putBodyParses || putBodyDraft.trim().length === 0
                    ? "border-glass-strong"
                    : "border-amber-400/40",
                )}
                rows={20}
              />
              <div className="flex flex-wrap items-center gap-2">
                <Button
                  size="sm"
                  onClick={() => sendCustomerPut.mutate()}
                  disabled={!putCanSend}
                >
                  <Send className="mr-1.5 h-3.5 w-3.5" />
                  {sendCustomerPut.isPending ? "Sending…" : "Send PUT"}
                </Button>
                <span className="text-[11px] text-muted-foreground">
                  Mutates the upstream customer immediately. Audited as{" "}
                  <span className="font-mono">debug.customer_put</span>.
                </span>
              </div>
            </div>
          ) : (
            <div className="rounded-md border border-amber-400/30 bg-amber-500/8 px-3 py-2 text-[11px] text-amber-200">
              No PUT body produced — the upstream GET did not return a parseable
              customer object.
              {putPreview.putBuildError && (
                <span className="ml-1 font-mono">({putPreview.putBuildError})</span>
              )}
            </div>
          )}

          {/* PUT result (after Send) */}
          {putResult && (
            <div className="space-y-2">
              <div className="flex flex-wrap items-center gap-2 text-xs">
                <span className="text-muted-foreground/70">Upstream PUT:</span>
                <span
                  className={cn(
                    "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5",
                    putResultStatusTone,
                  )}
                >
                  {putResult.status === 0 ? "no response" : `HTTP ${putResult.status}`}
                </span>
                {putResult.upstreamErrorCode && (
                  <span className="rounded border border-glass-strong bg-glass px-2 py-0.5 font-mono text-[11px] text-muted-foreground">
                    {putResult.upstreamErrorCode}
                  </span>
                )}
                <code className="truncate text-[11px] text-muted-foreground/80">
                  PUT {putResult.requestUrl}
                </code>
              </div>
              <div className="relative">
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => copyDebugBody(putResultFormatted)}
                  aria-label="Copy PUT response body"
                  className="absolute right-2 top-2 h-7 w-7 p-0"
                >
                  <Copy className="h-3.5 w-3.5" />
                </Button>
                <pre className="max-h-80 overflow-auto rounded-md border border-glass-strong bg-black/30 p-3 pr-10 font-mono text-[11px] leading-relaxed text-foreground/90">
                  {putResultFormatted || "(empty body)"}
                </pre>
              </div>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
