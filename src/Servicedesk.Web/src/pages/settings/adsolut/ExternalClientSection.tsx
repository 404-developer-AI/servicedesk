import { useMemo, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  Copy,
  Eye,
  EyeOff,
  KeyRound,
} from "lucide-react";
import { ApiError } from "@/lib/http";
import { adsolutApi, type AdsolutDebugAccessToken } from "@/lib/adsolut-api";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { serverNowMs } from "@/hooks/useServerTime";

export function ExternalClientSection() {
  // External-client (Postman / curl) card — token never persists in
  // localStorage / sessionStorage; lives only in this component's memory.
  const [externalToken, setExternalToken] = useState<AdsolutDebugAccessToken | null>(null);
  const [externalTokenVisible, setExternalTokenVisible] = useState(false);

  const fetchAccessToken = useMutation({
    mutationFn: () => adsolutApi.debugAccessToken(),
    onSuccess: (r) => {
      setExternalToken(r);
      setExternalTokenVisible(true);
    },
    onError: (err) => {
      setExternalToken(null);
      setExternalTokenVisible(false);
      toast.error(
        err instanceof ApiError
          ? `Token export failed (${err.status})`
          : "Token export failed",
      );
    },
  });

  const externalTokenMinutesLeft = useMemo(() => {
    if (!externalToken?.accessTokenExpiresUtc) return null;
    const t = new Date(externalToken.accessTokenExpiresUtc).getTime();
    if (Number.isNaN(t)) return null;
    return Math.round((t - serverNowMs()) / 60000);
  }, [externalToken?.accessTokenExpiresUtc]);

  const copyExternalToken = async () => {
    if (!externalToken) return;
    try {
      await navigator.clipboard.writeText(externalToken.accessToken);
      toast.success("Access token copied");
    } catch {
      toast.error("Could not copy — select the token manually");
    }
  };

  const copyPlain = async (value: string, label: string) => {
    try {
      await navigator.clipboard.writeText(value);
      toast.success(`${label} copied`);
    } catch {
      toast.error("Could not copy — select the value manually");
    }
  };

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          External client (Postman / curl)
        </h2>
        <p className="text-xs text-muted-foreground">
          Reveal the current access token to use Adsolut's API directly from
          Postman or curl. Token validity is ~1 hour — click again when it
          expires. Each reveal is recorded in the security audit log as{" "}
          <span className="font-mono">integration.adsolut.access_token.exported</span>{" "}
          so a leaked token can be traced back to who exported it.
        </p>
      </header>

      <div className="flex flex-wrap items-center gap-2">
        <Button
          size="sm"
          onClick={() => fetchAccessToken.mutate()}
          disabled={fetchAccessToken.isPending}
        >
          <KeyRound className="mr-1.5 h-3.5 w-3.5" />
          {fetchAccessToken.isPending
            ? "Fetching…"
            : externalToken
              ? "Refresh access token"
              : "Reveal access token"}
        </Button>
        {externalToken && (
          <Button
            size="sm"
            variant="ghost"
            onClick={() => {
              setExternalToken(null);
              setExternalTokenVisible(false);
            }}
            className="text-muted-foreground"
          >
            Clear
          </Button>
        )}
      </div>

      {externalToken && (
        <div className="mt-4 space-y-3">
          {/* Token row — eye-toggle to hide for shoulder-surfing safety,
              copy button for paste-into-Postman. */}
          <div className="space-y-1">
            <div className="flex items-center gap-2 text-[11px] text-muted-foreground/70">
              <span>Authorization: Bearer</span>
              {externalTokenMinutesLeft !== null && (
                <span
                  className={cn(
                    "rounded-full border px-2 py-0.5 text-[10px]",
                    externalTokenMinutesLeft > 5
                      ? "border-emerald-400/30 bg-emerald-500/10 text-emerald-300"
                      : externalTokenMinutesLeft > 0
                        ? "border-amber-400/30 bg-amber-500/8 text-amber-200"
                        : "border-rose-400/40 bg-rose-500/10 text-rose-300",
                  )}
                >
                  {externalTokenMinutesLeft > 0
                    ? `expires in ${externalTokenMinutesLeft} min`
                    : "expired — refresh"}
                </span>
              )}
            </div>
            <div className="flex items-center gap-2">
              <code className="flex-1 truncate rounded-md border border-glass-strong bg-black/30 px-3 py-2 font-mono text-[11px] text-foreground/90">
                {externalTokenVisible
                  ? externalToken.accessToken
                  : "•".repeat(64)}
              </code>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => setExternalTokenVisible((v) => !v)}
                className="h-9 w-9 shrink-0 p-0"
                aria-label={externalTokenVisible ? "Hide token" : "Show token"}
              >
                {externalTokenVisible ? (
                  <EyeOff className="h-3.5 w-3.5" />
                ) : (
                  <Eye className="h-3.5 w-3.5" />
                )}
              </Button>
              <Button
                size="sm"
                variant="ghost"
                onClick={copyExternalToken}
                className="h-9 w-9 shrink-0 p-0"
                aria-label="Copy access token"
              >
                <Copy className="h-3.5 w-3.5" />
              </Button>
            </div>
          </div>

          {/* Base URL + admin id — the other two pieces a Postman request needs. */}
          <dl className="grid grid-cols-1 gap-y-2 text-xs sm:grid-cols-[140px_1fr]">
            <dt className="text-muted-foreground/70">API base URL</dt>
            <dd className="flex items-center gap-2">
              <code className="flex-1 truncate rounded-md border border-glass-strong bg-black/30 px-2 py-1 font-mono text-[11px] text-foreground/90">
                {externalToken.baseUrl || "(not configured)"}
              </code>
              {externalToken.baseUrl && (
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => copyPlain(externalToken.baseUrl, "Base URL")}
                  className="h-7 w-7 shrink-0 p-0"
                  aria-label="Copy base URL"
                >
                  <Copy className="h-3.5 w-3.5" />
                </Button>
              )}
            </dd>
            <dt className="text-muted-foreground/70">Administration id</dt>
            <dd className="flex items-center gap-2">
              <code className="flex-1 truncate rounded-md border border-glass-strong bg-black/30 px-2 py-1 font-mono text-[11px] text-foreground/90">
                {externalToken.administrationId ?? "(no dossier active)"}
              </code>
              {externalToken.administrationId && (
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() =>
                    copyPlain(externalToken.administrationId!, "Administration id")
                  }
                  className="h-7 w-7 shrink-0 p-0"
                  aria-label="Copy administration id"
                >
                  <Copy className="h-3.5 w-3.5" />
                </Button>
              )}
            </dd>
          </dl>

          {/* One concrete example so the admin doesn't have to look up
              the URL pattern. Customers list is the safest GET to start
              with — any 200 here proves the token + admin id are wired
              correctly. */}
          {externalToken.baseUrl && externalToken.administrationId && (
            <div className="space-y-1">
              <div className="text-[11px] text-muted-foreground/70">
                Example — list first 10 customers
              </div>
              <div className="flex items-center gap-2">
                <code className="flex-1 truncate rounded-md border border-glass-strong bg-black/30 px-2 py-1 font-mono text-[11px] text-foreground/90">
                  GET {externalToken.baseUrl}/acc/v1/adm/{externalToken.administrationId}/customers?Limit=10
                </code>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() =>
                    copyPlain(
                      `${externalToken.baseUrl}/acc/v1/adm/${externalToken.administrationId}/customers?Limit=10`,
                      "Example URL",
                    )
                  }
                  className="h-7 w-7 shrink-0 p-0"
                  aria-label="Copy example URL"
                >
                  <Copy className="h-3.5 w-3.5" />
                </Button>
              </div>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
