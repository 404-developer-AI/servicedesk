import { useEffect, useMemo, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowLeft, Copy, Plug, RefreshCw, Search } from "lucide-react";
import {
  ApiError,
  adsolutApi,
  settingsApi,
  type AdsolutDebugKind,
  type AdsolutDebugLookupResponse,
  type AdsolutState,
  type SettingEntry,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";
import { AdsolutScopesPicker } from "@/components/settings/AdsolutScopesPicker";
import { IntegrationAuditLog } from "@/components/integrations/IntegrationAuditLog";
import { useIntegrationsSignalR } from "@/hooks/useIntegrationsSignalR";
import { cn } from "@/lib/utils";

const ADSOLUT_SETTINGS_QUERY_KEY = ["settings", "list", "Adsolut"] as const;
const ADSOLUT_STATUS_QUERY_KEY = ["integrations", "adsolut", "status"] as const;
const ADSOLUT_SECRET_QUERY_KEY = ["integrations", "adsolut", "secret"] as const;
const ADSOLUT_ADMIN_QUERY_KEY = ["integrations", "adsolut", "administrations"] as const;
const ADSOLUT_SYNC_QUERY_KEY = ["integrations", "adsolut", "sync"] as const;

const STATE_LABEL: Record<AdsolutState, { tone: string; text: string; dot: string }> = {
  not_configured: {
    tone: "border-white/15 bg-white/[0.06] text-muted-foreground",
    text: "Not configured",
    dot: "bg-white/40",
  },
  not_connected: {
    tone: "border-amber-400/30 bg-amber-500/[0.08] text-amber-200",
    text: "Not connected",
    dot: "bg-amber-400",
  },
  connected: {
    tone: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    text: "Connected",
    dot: "bg-emerald-400",
  },
  sync_failing: {
    tone: "border-amber-400/30 bg-amber-500/[0.08] text-amber-200",
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

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

function formatDate(iso: string | null | undefined) {
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

function daysUntil(iso: string | null | undefined): number | null {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return null;
  return Math.round((t - Date.now()) / (24 * 60 * 60 * 1000));
}

function statusFromQuery(): string | null {
  if (typeof window === "undefined") return null;
  const params = new URLSearchParams(window.location.search);
  return params.get("status");
}

function clearStatusFromUrl() {
  if (typeof window === "undefined") return;
  const url = new URL(window.location.href);
  if (!url.searchParams.has("status")) return;
  url.searchParams.delete("status");
  window.history.replaceState({}, "", url.toString());
}

export function AdsolutIntegrationPage() {
  const qc = useQueryClient();
  // Live status pushes — a connect/disconnect/refresh-failed transition
  // on another tab flips this page's badges within seconds.
  useIntegrationsSignalR();

  const status = useQuery({
    queryKey: ADSOLUT_STATUS_QUERY_KEY,
    queryFn: () => adsolutApi.status(),
  });
  const settingsList = useQuery({
    queryKey: ADSOLUT_SETTINGS_QUERY_KEY,
    queryFn: () => settingsApi.list("Adsolut"),
  });
  const secret = useQuery({
    queryKey: ADSOLUT_SECRET_QUERY_KEY,
    queryFn: () => adsolutApi.secretStatus(),
  });

  // Only fetch the dossier list once we are actually connected — no point
  // calling the Administrations API while the integration is in
  // not_configured / not_connected state (it would just 401).
  const isConnectedState =
    status.data?.state === "connected" ||
    status.data?.state === "refresh_failed" ||
    status.data?.state === "sync_failing";
  const administrations = useQuery({
    queryKey: ADSOLUT_ADMIN_QUERY_KEY,
    queryFn: () => adsolutApi.listAdministrations(),
    enabled: isConnectedState,
  });
  const syncState = useQuery({
    queryKey: ADSOLUT_SYNC_QUERY_KEY,
    queryFn: () => adsolutApi.syncState(),
    enabled: isConnectedState,
  });

  const [secretValue, setSecretValue] = useState("");
  const [refreshOutcome, setRefreshOutcome] = useState<string | null>(null);
  const [dossierPick, setDossierPick] = useState<string>("");
  const [debugKind, setDebugKind] = useState<AdsolutDebugKind>("customer");
  const [debugCode, setDebugCode] = useState<string>("");
  const [debugResult, setDebugResult] = useState<AdsolutDebugLookupResponse | null>(null);

  // Surface the post-callback ?status= query param as a toast, then strip it
  // from the URL so a refresh doesn't fire it again.
  useEffect(() => {
    const code = statusFromQuery();
    if (!code) return;
    if (code === "connected") {
      toast.success("Connected to Adsolut");
    } else {
      toast.error(`Adsolut connect failed: ${code.replaceAll("_", " ")}`);
    }
    clearStatusFromUrl();
    qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
  }, [qc]);

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

  const saveSecret = useMutation({
    mutationFn: () => adsolutApi.setSecret(secretValue),
    onSuccess: () => {
      toast.success("Client secret saved");
      setSecretValue("");
      qc.invalidateQueries({ queryKey: ADSOLUT_SECRET_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Save failed (${err.status})` : "Save failed"),
  });

  const deleteSecret = useMutation({
    mutationFn: () => adsolutApi.deleteSecret(),
    onSuccess: () => {
      toast.success("Client secret cleared");
      qc.invalidateQueries({ queryKey: ADSOLUT_SECRET_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
    },
  });

  const selectAdministration = useMutation({
    mutationFn: (id: string) => adsolutApi.selectAdministration(id),
    onSuccess: () => {
      toast.success("Adsolut dossier activated");
      setDossierPick("");
      qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ADSOLUT_ADMIN_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ADSOLUT_SYNC_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(
        err instanceof ApiError ? `Activate failed (${err.status})` : "Activate failed",
      ),
  });

  const triggerSync = useMutation({
    mutationFn: () => adsolutApi.triggerSync(),
    onSuccess: () => {
      toast.success("Sync queued — will run within 2 seconds");
      // Audit log + sync-state will refresh on the SignalR push when the
      // tick lands; nothing to invalidate here yet.
    },
    onError: (err) =>
      toast.error(
        err instanceof ApiError ? `Sync request failed (${err.status})` : "Sync request failed",
      ),
  });

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

  const copyRedirect = async (url: string) => {
    try {
      await navigator.clipboard.writeText(url);
      toast.success("Redirect URI copied");
    } catch {
      toast.error("Could not copy — copy it manually");
    }
  };

  const copyDebugBody = async (body: string) => {
    try {
      await navigator.clipboard.writeText(body);
      toast.success("Response copied");
    } catch {
      toast.error("Could not copy — select the JSON manually");
    }
  };

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
    if (s >= 400 && s < 500) return "border-amber-400/30 bg-amber-500/[0.08] text-amber-200";
    return "border-rose-400/40 bg-rose-500/10 text-rose-300";
  })();

  const debugCanLookup =
    debugCode.trim().length > 0 && debugCode.trim().length <= 32 && !debugLookup.isPending;

  const slidingExpiryDays = useMemo(() => {
    const d = status.data?.lastRefreshedUtc;
    if (!d) return null;
    const expiry = new Date(d).getTime() + REFRESH_WINDOW_DAYS * 24 * 60 * 60 * 1000;
    return Math.round((expiry - Date.now()) / (24 * 60 * 60 * 1000));
  }, [status.data?.lastRefreshedUtc]);

  if (status.isLoading || settingsList.isLoading || secret.isLoading) {
    return <Skeleton className="h-96 w-full" />;
  }

  const s = status.data;
  if (!s) {
    return (
      <div className="rounded-md border border-rose-400/30 bg-rose-500/[0.08] p-4 text-sm text-rose-200">
        Could not load Adsolut status. Refresh the page or check the API logs.
      </div>
    );
  }

  const stateBadge = STATE_LABEL[s.state];
  const isConnected =
    s.state === "connected" || s.state === "refresh_failed" || s.state === "sync_failing";
  const canConnect = s.clientIdConfigured && s.clientSecretConfigured;

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <Link
            to="/settings/integrations"
            className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground"
          >
            <ArrowLeft className="h-3.5 w-3.5" />
            Back to Integrations
          </Link>
          <div className="mb-2 text-primary">
            <Plug className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Adsolut CRM</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            One-time admin authorization against the Wolters Kluwer login. The granted
            refresh token is stored encrypted and rotated on every use; one Adsolut
            administration links to this servicedesk install.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      {/* Connection status */}
      <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
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
          <div className="mb-4 rounded-md border border-amber-500/30 bg-amber-500/[0.08] px-3 py-2 text-[11px] text-amber-200">
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
            {syncState.data?.nextSyncUtc && s.administrationId ? (
              <>
                <dt className="text-muted-foreground/70">Next sync</dt>
                <dd className="text-foreground">
                  {formatDate(syncState.data.nextSyncUtc)}
                  <span className="ml-2 text-muted-foreground/60">
                    (every {syncState.data.intervalMinutes} min)
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

      {/* Configuration */}
      <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
        <header className="mb-4 space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Configuration
          </h2>
          <p className="text-xs text-muted-foreground">
            Wolters Kluwer provisions a client_id + secret per servicedesk install; the
            redirect URI below must match exactly what was registered. Switching environment
            requires a disconnect + reconnect — refresh tokens issued by UAT do not work
            against production.
          </p>
        </header>

        <div className="space-y-3">
          {(["Adsolut.Environment", "Adsolut.ClientId"] as const).map((key) => {
            const entry = findEntry(settingsList.data, key);
            if (!entry) return null;
            return (
              <SettingField
                key={key}
                entry={entry}
                queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
              />
            );
          })}

          <AdsolutScopesPicker
            entry={findEntry(settingsList.data, "Adsolut.Scopes")}
            queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
            needsReconnect={s.scopesNeedReconnect}
          />

          <div className="flex items-center justify-between gap-3 rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2">
            <div className="min-w-0 flex-1">
              <div className="text-sm">Client secret</div>
              <div className="text-xs text-muted-foreground">
                {secret.data?.configured ? "Configured (encrypted at rest)" : "Not configured"}
              </div>
            </div>
            <Input
              type="password"
              placeholder={secret.data?.configured ? "••••••• (enter to replace)" : "Paste secret"}
              value={secretValue}
              onChange={(e) => setSecretValue(e.target.value)}
              className="max-w-xs"
            />
            <Button
              size="sm"
              onClick={() => saveSecret.mutate()}
              disabled={!secretValue || saveSecret.isPending}
            >
              {saveSecret.isPending ? "Saving…" : "Save"}
            </Button>
            {secret.data?.configured && (
              <Button
                size="sm"
                variant="ghost"
                onClick={() => deleteSecret.mutate()}
                disabled={deleteSecret.isPending}
              >
                Clear
              </Button>
            )}
          </div>

          <div className="flex items-center justify-between gap-3 rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2">
            <div className="min-w-0 flex-1">
              <div className="text-sm">Redirect URI</div>
              <div className="text-xs text-muted-foreground">
                Register this exact value in your Wolters Kluwer client. Auto-derived from{" "}
                <span className="font-mono">App.PublicBaseUrl</span>.
              </div>
            </div>
            <code className="truncate rounded bg-white/[0.04] px-2 py-1 font-mono text-xs">
              {s.redirectUri}
            </code>
            <Button
              size="sm"
              variant="ghost"
              onClick={() => copyRedirect(s.redirectUri)}
              aria-label="Copy redirect URI"
            >
              <Copy className="h-3.5 w-3.5" />
            </Button>
          </div>
        </div>
      </section>

      {/* v0.0.26 — Dossier picker. Only relevant once the integration is
          connected. The admin must explicitly activate one Adsolut
          administration before sync ticks do real work; the worker skips
          ticks while administrationId is null. */}
      {isConnectedState && (
        <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
          <header className="mb-4 space-y-1">
            <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
              Dossier
            </h2>
            <p className="text-xs text-muted-foreground">
              Pick which Adsolut administration this servicedesk install syncs against.
              Activating a dossier registers this integration with Wolters Kluwer; the
              disconnect flow deactivates it again so it does not keep generating billable
              activity at WK after the install stops using it.
            </p>
          </header>

          {administrations.isLoading ? (
            <Skeleton className="h-10 w-full" />
          ) : administrations.isError ? (
            <div className="rounded-md border border-rose-400/30 bg-rose-500/[0.08] px-3 py-2 text-xs text-rose-200">
              Could not list dossiers — refresh-token may be expired. Try Test refresh first.
            </div>
          ) : (
            <div className="space-y-3">
              {s.administrationId ? (
                <div className="rounded-md border border-emerald-400/30 bg-emerald-500/[0.06] px-3 py-2 text-xs text-emerald-200">
                  Active dossier:{" "}
                  <span className="font-mono">
                    {administrations.data?.items.find((a) => a.id === s.administrationId)?.name ??
                      s.administrationId}
                  </span>
                  {(() => {
                    const found = administrations.data?.items.find(
                      (a) => a.id === s.administrationId,
                    );
                    return found?.code ? (
                      <span className="ml-2 text-emerald-300/70">({found.code})</span>
                    ) : null;
                  })()}
                </div>
              ) : (
                <div className="rounded-md border border-amber-400/30 bg-amber-500/[0.08] px-3 py-2 text-xs text-amber-200">
                  No dossier picked yet — sync ticks are paused. Choose one below to start
                  pulling Customers from Adsolut.
                </div>
              )}

              <div className="flex flex-wrap items-center gap-2">
                <Select
                  value={dossierPick || s.administrationId || ""}
                  onValueChange={(value) => setDossierPick(value)}
                >
                  <SelectTrigger className="min-w-[16rem] flex-1">
                    <SelectValue placeholder="Select a dossier…" />
                  </SelectTrigger>
                  <SelectContent>
                    {(administrations.data?.items ?? []).map((a) => (
                      <SelectItem key={a.id} value={a.id}>
                        {a.name}
                        {a.code ? ` (${a.code})` : ""}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Button
                  size="sm"
                  onClick={() =>
                    dossierPick && selectAdministration.mutate(dossierPick)
                  }
                  disabled={
                    !dossierPick ||
                    dossierPick === s.administrationId ||
                    selectAdministration.isPending
                  }
                >
                  {selectAdministration.isPending ? "Activating…" : "Activate dossier"}
                </Button>
              </div>
            </div>
          )}
        </section>
      )}

      {/* v0.0.26 — Sync panel. Counters + last-tick timestamps + a "Sync now"
          button so admins do not have to wait the full interval after
          changing a toggle. */}
      {isConnectedState && s.administrationId && (
        <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
          <header className="mb-4 flex items-start justify-between gap-4">
            <div className="space-y-1">
              <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
                Sync
              </h2>
              <p className="text-xs text-muted-foreground">
                Bidirectional Adsolut ↔ servicedesk Companies sync. Pull and Push toggles are
                independent — an admin can keep updates flowing in both directions while never
                creating new rows on either side. All gating-toggles default OFF and are reset
                to OFF on every (re)connect; opt in once you've verified the dossier mapping.
              </p>
            </div>
            <Button
              size="sm"
              onClick={() => triggerSync.mutate()}
              disabled={triggerSync.isPending}
            >
              <RefreshCw className="mr-1.5 h-3.5 w-3.5" />
              {triggerSync.isPending ? "Queueing…" : "Sync now"}
            </Button>
          </header>

          <dl className="mb-4 grid grid-cols-1 gap-y-2 text-xs sm:grid-cols-2">
            <dt className="text-muted-foreground/70">Last delta sync</dt>
            <dd className="text-foreground">{formatDate(syncState.data?.lastDeltaSyncUtc)}</dd>
            <dt className="text-muted-foreground/70">Last full sync</dt>
            <dd className="text-foreground">{formatDate(syncState.data?.lastFullSyncUtc)}</dd>
            <dt className="text-muted-foreground/70">Companies seen (last tick)</dt>
            <dd className="text-foreground tabular-nums">
              {syncState.data?.companiesSeen ?? 0}
            </dd>
            <dt className="text-muted-foreground/70">Companies upserted (last tick)</dt>
            <dd className="text-foreground tabular-nums">
              {syncState.data?.companiesUpserted ?? 0}
            </dd>
            <dt className="text-muted-foreground/70">Skipped — local edit newer</dt>
            <dd className="text-foreground tabular-nums">
              {syncState.data?.companiesSkippedLoserInConflict ?? 0}
            </dd>
            {syncState.data?.lastError && (
              <>
                <dt className="text-muted-foreground/70">Last error</dt>
                <dd className="text-rose-300">
                  {syncState.data.lastError}
                  {syncState.data.lastErrorUtc && (
                    <span className="ml-2 text-muted-foreground/60">
                      ({formatDate(syncState.data.lastErrorUtc)})
                    </span>
                  )}
                </dd>
              </>
            )}
          </dl>

          <div className="space-y-5">
            <div className="space-y-2">
              {(
                ["Adsolut.Sync.IntervalMinutes"] as const
              ).map((key) => {
                const entry = findEntry(settingsList.data, key);
                if (!entry) return null;
                return (
                  <SettingField
                    key={key}
                    entry={entry}
                    queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
                  />
                );
              })}
            </div>

            <div>
              <h3 className="mb-2 text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
                Pull · Adsolut → servicedesk
              </h3>
              <div className="space-y-2">
                {(
                  [
                    "Adsolut.Sync.Pull.Companies.Update",
                    "Adsolut.Sync.Pull.Companies.Create",
                  ] as const
                ).map((key) => {
                  const entry = findEntry(settingsList.data, key);
                  if (!entry) return null;
                  return (
                    <SettingField
                      key={key}
                      entry={entry}
                      queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
                    />
                  );
                })}
              </div>
            </div>

            <div>
              <h3 className="mb-2 text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
                Push · Servicedesk → Adsolut
              </h3>
              <div className="space-y-2">
                {(
                  [
                    "Adsolut.Push.UpdateExistingCustomers",
                    "Adsolut.Push.CreateNewCustomers",
                  ] as const
                ).map((key) => {
                  const entry = findEntry(settingsList.data, key);
                  if (!entry) return null;
                  return (
                    <SettingField
                      key={key}
                      entry={entry}
                      queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
                    />
                  );
                })}
              </div>
            </div>

            {/* Suppliers — placeholder for v0.0.28. The IncludeSuppliers toggle and
                the two push-toggles are seeded so the v0.0.28 unlock is a code flip,
                not a schema migration. The backend force-ignores them in v0.0.27. */}
            <div>
              <h3 className="mb-2 flex items-center gap-2 text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
                Suppliers
                <span className="rounded-full border border-amber-400/30 bg-amber-500/[0.08] px-2 py-0.5 text-[9px] uppercase tracking-wider text-amber-200">
                  In development
                </span>
              </h3>
              <p className="mb-2 text-xs text-muted-foreground/70">
                Bidirectional Suppliers sync is targeted for the next release. These toggles
                are visible but the worker ignores them until v0.0.28 lands.
              </p>
              <div className="space-y-2 opacity-60">
                {(
                  [
                    "Adsolut.Sync.IncludeSuppliers",
                    "Adsolut.Push.UpdateExistingSuppliers",
                    "Adsolut.Push.CreateNewSuppliers",
                  ] as const
                ).map((key) => {
                  const entry = findEntry(settingsList.data, key);
                  if (!entry) return null;
                  return (
                    <SettingField
                      key={key}
                      entry={entry}
                      queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
                      readOnly
                    />
                  );
                })}
              </div>
            </div>

            <div>
              <h3 className="mb-2 text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
                Behavior
              </h3>
              <div className="space-y-2">
                {(
                  ["Adsolut.Sync.LinkCompanyDomainsFromEmail"] as const
                ).map((key) => {
                  const entry = findEntry(settingsList.data, key);
                  if (!entry) return null;
                  return (
                    <SettingField
                      key={key}
                      entry={entry}
                      queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
                    />
                  );
                })}
              </div>
            </div>
          </div>
        </section>
      )}

      {/* API debug — admin probe for /customers or /suppliers by Code. Only
          useful once a dossier is active because Adsolut returns nothing
          without one. Strict whitelist on the backend means this card cannot
          be turned into a generic API proxy. */}
      {isConnectedState && s.administrationId && (
        <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
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
              className="inline-flex rounded-md border border-white/[0.08] bg-white/[0.03] p-0.5 text-xs"
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
                      ? "bg-white/[0.08] text-foreground shadow-sm"
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
                  <span className="rounded border border-white/[0.08] bg-white/[0.03] px-2 py-0.5 font-mono text-[11px] text-muted-foreground">
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
                <pre className="max-h-[28rem] overflow-auto rounded-md border border-white/[0.06] bg-black/30 p-3 pr-10 font-mono text-[11px] leading-relaxed text-foreground/90">
                  {debugFormattedBody || "(empty body)"}
                </pre>
              </div>
            </div>
          )}
        </section>
      )}

      {/* Audit log — operational call history */}
      <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
        <header className="mb-4 space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Audit log
          </h2>
          <p className="text-xs text-muted-foreground">
            Every outbound call to Wolters Kluwer plus each healthcheck tick lands here with
            its latency and any upstream error. Distinct from the security audit log on
            <span className="font-mono"> /settings/audit</span> — this view tracks
            integration health, not admin actions.
          </p>
        </header>
        <IntegrationAuditLog integration="adsolut" />
      </section>
    </div>
  );
}
