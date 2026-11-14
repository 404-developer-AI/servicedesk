import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ApiError,
  adsolutApi,
  type AdsolutState,
  type AdsolutStatus,
  type AdsolutSyncState,
} from "@/lib/api";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { serverNowMs } from "@/hooks/useServerTime";
import { ADSOLUT_STATUS_QUERY_KEY, formatDate } from "@/pages/settings/adsolut/shared";

const STATE_LABEL: Record<AdsolutState, { tone: string; text: string; dot: string }> = {
  not_configured: {
    tone: "border-glass-strong bg-glass-strong text-muted-foreground",
    text: "Not configured",
    dot: "bg-glass-strong",
  },
  not_connected: {
    tone: "border-amber-400/30 bg-amber-500/8 text-amber-200",
    text: "Not connected",
    dot: "bg-amber-400",
  },
  connected: {
    tone: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    text: "Connected",
    dot: "bg-emerald-400",
  },
  sync_failing: {
    tone: "border-amber-400/30 bg-amber-500/8 text-amber-200",
    text: "Sync failing",
    dot: "bg-amber-400",
  },
  refresh_failed: {
    tone: "border-rose-400/40 bg-rose-500/10 text-rose-300",
    text: "Reconnect required",
    dot: "bg-rose-400",
  },
};

// Adsolut refresh-token sliding window: 1 month per the docs. Recompute
// the human-friendly hint client-side from lastRefreshedUtc — server-side
// math is unnecessary because this is purely informational.
const REFRESH_WINDOW_DAYS = 30;

function daysUntil(iso: string | null | undefined): number | null {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return null;
  return Math.round((t - serverNowMs()) / (24 * 60 * 60 * 1000));
}

export function ConnectionSection({
  status: s,
  syncState,
}: {
  status: AdsolutStatus;
  syncState: AdsolutSyncState | undefined;
}) {
  const qc = useQueryClient();

  const [refreshOutcome, setRefreshOutcome] = useState<string | null>(null);

  const startConnect = useMutation({
    mutationFn: () => adsolutApi.startAuthorize(),
    onSuccess: (r) => {
      window.location.href = r.authorizeUrl;
    },
    onError: (err) => {
      toast.error(
        err instanceof ApiError
          ? `Connect failed (${err.status})`
          : "Connect failed — check the configuration",
      );
    },
  });

  const disconnect = useMutation({
    mutationFn: () => adsolutApi.disconnect(),
    onSuccess: () => {
      toast.success("Disconnected from Adsolut");
      qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Disconnect failed (${err.status})` : "Disconnect failed"),
  });

  const testRefresh = useMutation({
    mutationFn: () => adsolutApi.refresh(),
    onSuccess: (r) => {
      if (r.ok) {
        setRefreshOutcome(`OK — token expires ${formatDate(r.expiresUtc)}`);
        qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
      } else if (r.requiresReconnect) {
        setRefreshOutcome(`Reconnect required: ${r.upstreamErrorCode ?? "invalid_grant"}`);
        qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
      } else {
        setRefreshOutcome(`FAILED: ${r.upstreamErrorCode ?? r.message ?? "unknown"}`);
      }
    },
    onError: (err) => {
      setRefreshOutcome(err instanceof ApiError ? `FAILED — HTTP ${err.status}` : "FAILED");
    },
  });

  const slidingExpiryDays = useMemo(() => {
    const d = s.lastRefreshedUtc;
    if (!d) return null;
    const expiry = new Date(d).getTime() + REFRESH_WINDOW_DAYS * 24 * 60 * 60 * 1000;
    return Math.round((expiry - serverNowMs()) / (24 * 60 * 60 * 1000));
  }, [s.lastRefreshedUtc]);

  const stateBadge = STATE_LABEL[s.state];
  const isConnected =
    s.state === "connected" || s.state === "refresh_failed" || s.state === "sync_failing";
  const canConnect = s.clientIdConfigured && s.clientSecretConfigured;

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Connection
          </h2>
          <p className="text-xs text-muted-foreground">
            Server-to-server flow. After connecting, this servicedesk install can call
            the Adsolut API on behalf of the authorizing user without further prompts
            until the refresh token's sliding 1-month window lapses.
          </p>
        </div>
        <span
          className={cn(
            "inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full border px-2.5 py-0.5 text-xs",
            stateBadge.tone,
          )}
        >
          <span className={cn("h-1.5 w-1.5 rounded-full", stateBadge.dot)} />
          {stateBadge.text}
        </span>
      </header>

      {!canConnect && (
        <div className="mb-4 rounded-md border border-amber-500/30 bg-amber-500/8 px-3 py-2 text-[11px] text-amber-200">
          Fill in <span className="font-mono">Adsolut.ClientId</span> and the client secret
          below before connecting. Wolters Kluwer must also have a redirect URI of{" "}
          <span className="font-mono">{s.redirectUri}</span> registered for this client.
        </div>
      )}

      {isConnected && (
        <dl className="mb-4 grid grid-cols-1 gap-y-2 text-xs sm:grid-cols-2">
          <dt className="text-muted-foreground/70">Authorized as</dt>
          <dd className="text-foreground">
            {s.authorizedEmail ?? s.authorizedSubject ?? "(unknown subject)"}
          </dd>
          <dt className="text-muted-foreground/70">Authorized at</dt>
          <dd className="text-foreground">{formatDate(s.authorizedUtc)}</dd>
          <dt className="text-muted-foreground/70">Last refreshed</dt>
          <dd className="text-foreground">{formatDate(s.lastRefreshedUtc)}</dd>
          <dt className="text-muted-foreground/70">Access token expires</dt>
          <dd className="text-foreground">
            {formatDate(s.accessTokenExpiresUtc)}
            {(() => {
              const d = daysUntil(s.accessTokenExpiresUtc);
              if (d === null) return null;
              if (d < 0) return <span className="ml-2 text-amber-300">(expired — refresh on next call)</span>;
              return null;
            })()}
          </dd>
          <dt className="text-muted-foreground/70">Refresh window</dt>
          <dd className="text-foreground">
            {slidingExpiryDays === null
              ? "—"
              : slidingExpiryDays > 0
                ? `${slidingExpiryDays} day${slidingExpiryDays === 1 ? "" : "s"} left (sliding 1-month)`
                : "Expired — admin must reconnect"}
          </dd>
          {syncState?.nextSyncUtc && s.administrationId ? (
            <>
              <dt className="text-muted-foreground/70">Next sync</dt>
              <dd className="text-foreground">
                {formatDate(syncState.nextSyncUtc)}
                <span className="ml-2 text-muted-foreground/60">
                  (every {syncState.intervalMinutes} min)
                </span>
              </dd>
            </>
          ) : null}
          {s.lastRefreshError && (
            <>
              <dt className="text-muted-foreground/70">Last refresh error</dt>
              <dd className="text-rose-300">
                {s.lastRefreshError}
                {s.lastRefreshErrorUtc && (
                  <span className="ml-2 text-muted-foreground/60">
                    ({formatDate(s.lastRefreshErrorUtc)})
                  </span>
                )}
              </dd>
            </>
          )}
        </dl>
      )}

      <div className="flex flex-wrap items-center gap-2">
        {!isConnected ? (
          <Button
            onClick={() => startConnect.mutate()}
            disabled={!canConnect || startConnect.isPending}
          >
            {startConnect.isPending ? "Redirecting…" : "Connect to Adsolut"}
          </Button>
        ) : (
          <>
            <Button
              onClick={() => startConnect.mutate()}
              disabled={!canConnect || startConnect.isPending}
            >
              {startConnect.isPending ? "Redirecting…" : "Reconnect"}
            </Button>
            <Button
              onClick={() => testRefresh.mutate()}
              disabled={testRefresh.isPending}
              variant="ghost"
            >
              {testRefresh.isPending ? "Refreshing…" : "Test refresh"}
            </Button>
            <Button
              onClick={() => disconnect.mutate()}
              disabled={disconnect.isPending}
              variant="ghost"
              className="text-rose-300 hover:text-rose-200"
            >
              {disconnect.isPending ? "Disconnecting…" : "Disconnect"}
            </Button>
          </>
        )}
        {refreshOutcome && (
          <span
            className={cn(
              "ml-2 text-xs",
              refreshOutcome.startsWith("OK") ? "text-emerald-300" : "text-rose-300",
            )}
          >
            {refreshOutcome}
          </span>
        )}
      </div>
    </section>
  );
}
