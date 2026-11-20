import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowLeft,
  BadgeCheck,
  CheckCircle2,
  Clock,
  Fingerprint,
  Globe,
  KeyRound,
  RefreshCw,
  Trash2,
} from "lucide-react";
import { ApiError, apiErrorMessage } from "@/lib/api";
import { tdSynnexAdminApi, type TdSynnexState } from "@/lib/order-management-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { IntegrationAuditLog } from "@/components/integrations/IntegrationAuditLog";
import tdsynnexLogo from "@/assets/integrations/tdsynnex.svg";
import { cn } from "@/lib/utils";
import { formatTimeAgo } from "@/lib/format";
import { formatDate } from "@/lib/dateFormat";
import { useServerTimeZone } from "@/hooks/useServerTime";

const STATUS_QK = ["integrations", "tdsynnex", "status"] as const;

const STATE_LABEL: Record<TdSynnexState, { tone: string; text: string; dot: string }> = {
  disabled: {
    tone: "border-glass-strong bg-glass-strong text-muted-foreground",
    text: "Disabled",
    dot: "bg-glass-strong",
  },
  "not-configured": {
    tone: "border-amber-400/30 bg-amber-500/8 text-amber-200",
    text: "Not configured",
    dot: "bg-amber-400",
  },
  ready: {
    tone: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    text: "Ready",
    dot: "bg-emerald-400",
  },
};

function relativeFromUtc(iso: string | null | undefined): string | null {
  return iso ? formatTimeAgo(iso, { fallback: "" }) || null : null;
}

/// Settings → Integrations → TD SYNNEX (v0.1.59). Configures the EU
/// Resellers API connection that feeds Order Management. Secrets are
/// write-only; the base / token URL must be https on a tdsynnex.com host
/// (enforced server-side).
export function TdSynnexIntegrationPage() {
  const qc = useQueryClient();
  const status = useQuery({ queryKey: STATUS_QK, queryFn: tdSynnexAdminApi.status });
  const zone = useServerTimeZone();

  const [clientIdDraft, setClientIdDraft] = useState("");
  const [secretDraft, setSecretDraft] = useState("");
  // Drafts are null until edited, so the fields show the saved value
  // without copying server state into local state.
  const [baseUrlEdit, setBaseUrlDraft] = useState<string | null>(null);
  const [tokenUrlEdit, setTokenUrlDraft] = useState<string | null>(null);
  const [intervalEdit, setIntervalDraft] = useState<string | null>(null);
  const [initialDaysEdit, setInitialDaysDraft] = useState<string | null>(null);
  const [activeEdit, setActiveDraft] = useState<string | null>(null);

  const data = status.data;
  const baseUrlDraft = baseUrlEdit ?? data?.baseUrl ?? "";
  const tokenUrlDraft = tokenUrlEdit ?? data?.tokenUrl ?? "";
  const intervalDraft = intervalEdit ?? String(data?.syncIntervalMinutes ?? "");
  const initialDaysDraft = initialDaysEdit ?? String(data?.initialSyncDays ?? "");
  const activeDraft = activeEdit ?? String(data?.activeRefreshMinutes ?? "");
  const resetDrafts = () => {
    setBaseUrlDraft(null);
    setTokenUrlDraft(null);
    setIntervalDraft(null);
    setInitialDaysDraft(null);
    setActiveDraft(null);
  };
  const enabled = data?.enabled ?? false;
  const stateMeta = STATE_LABEL[data?.state ?? "disabled"];
  const hasClientId = data?.clientIdConfigured ?? false;
  const hasSecret = data?.clientSecretConfigured ?? false;
  const credentialsSet = hasClientId && hasSecret;
  const connectionReady = credentialsSet && (data?.baseUrl ?? "").length > 0;

  const onErr = (err: unknown) => {
    const upstream = apiErrorMessage(err);
    toast.error(upstream ?? (err instanceof ApiError ? `Failed (${err.status})` : "Failed"));
  };
  const refresh = () => qc.invalidateQueries({ queryKey: STATUS_QK });

  const saveCredentials = useMutation({
    mutationFn: () => tdSynnexAdminApi.setCredentials(clientIdDraft.trim() || null, secretDraft.trim() || null),
    onSuccess: () => {
      toast.success("TD SYNNEX credentials saved");
      setClientIdDraft("");
      setSecretDraft("");
      refresh();
    },
    onError: onErr,
  });

  const clearCredentials = useMutation({
    mutationFn: () => tdSynnexAdminApi.deleteCredentials(),
    onSuccess: () => {
      toast.success("TD SYNNEX credentials cleared");
      refresh();
    },
    onError: onErr,
  });

  const toggleEnabled = useMutation({
    mutationFn: (next: boolean) => tdSynnexAdminApi.setEnabled(next),
    onSuccess: (_d, next) => {
      toast.success(next ? "Integration enabled" : "Integration disabled");
      refresh();
    },
    onError: onErr,
  });

  const saveConnection = useMutation({
    mutationFn: () => tdSynnexAdminApi.setConfig({ baseUrl: baseUrlDraft.trim(), tokenUrl: tokenUrlDraft.trim() }),
    onSuccess: async () => {
      toast.success("Connection settings saved");
      await refresh();
      setBaseUrlDraft(null);
      setTokenUrlDraft(null);
    },
    onError: onErr,
  });

  const saveSchedule = useMutation({
    mutationFn: () =>
      tdSynnexAdminApi.setConfig({
        syncIntervalMinutes: Number(intervalDraft),
        initialSyncDays: Number(initialDaysDraft),
        activeRefreshMinutes: Number(activeDraft),
      }),
    onSuccess: async () => {
      toast.success("Sync schedule saved");
      await refresh();
      resetDrafts();
    },
    onError: onErr,
  });

  const test = useMutation({
    mutationFn: () => tdSynnexAdminApi.test(),
    onSuccess: (r) =>
      toast.success(
        `Connected — ${r.totalResults} order${r.totalResults === 1 ? "" : "s"} in the last 30 days (list key "${r.listKey ?? "?"}")`,
      ),
    onError: onErr,
  });

  const runSync = useMutation({
    mutationFn: () => tdSynnexAdminApi.sync(),
    onSuccess: (r) => {
      if (r.success) toast.success(`Synced — ${r.seen} orders read, ${r.changed} changed`);
      else if (r.status === "busy") toast.message("A sync is already running.");
      else toast.error(r.error ?? "Sync failed");
      refresh();
    },
    onError: onErr,
  });

  const connectionDirty =
    !!data && (baseUrlDraft.trim() !== data.baseUrl || tokenUrlDraft.trim() !== data.tokenUrl);
  const scheduleValid =
    Number(intervalDraft) >= 10 &&
    initialDaysDraft.trim() !== "" && Number(initialDaysDraft) >= 0 && Number(initialDaysDraft) <= 3650 &&
    Number(activeDraft) >= 60;
  const scheduleDirty =
    !!data &&
    (Number(intervalDraft) !== data.syncIntervalMinutes ||
      Number(initialDaysDraft) !== data.initialSyncDays ||
      Number(activeDraft) !== data.activeRefreshMinutes);

  const lastChecked = relativeFromUtc(data?.lastCheckedUtc);
  const lastSuccess = relativeFromUtc(data?.lastSuccessUtc);

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <Link
            to="/settings/integrations"
            className="inline-flex items-center gap-1 text-xs text-muted-foreground/70 transition-colors hover:text-foreground"
          >
            <ArrowLeft className="h-3 w-3" /> Integrations
          </Link>
          <div className="mb-2 flex h-10 w-10 items-center justify-center rounded-lg border border-glass-strong bg-glass">
            <img src={tdsynnexLogo} alt="" aria-hidden className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">TD SYNNEX</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            Reads your TD SYNNEX orders through the EU Resellers API and mirrors them into{" "}
            <strong>Order Management</strong>: status, expected ship dates per split delivery,
            delivery notes and invoices. Agents with the Order Management flag can link orders to
            tickets; a change on a linked order can run triggers on the ticket.
          </p>
        </div>
        <Badge className={cn("border text-xs font-normal", stateMeta.tone)}>
          <span className={cn("mr-1.5 inline-block h-1.5 w-1.5 rounded-full", stateMeta.dot)} />
          {stateMeta.text}
        </Badge>
      </header>

      <section className="rounded-lg border border-glass bg-glass p-5">
        <div className="flex items-center justify-between gap-4">
          <div>
            <h2 className="text-sm font-medium text-foreground">Enabled</h2>
            <p className="text-xs text-muted-foreground">
              Master switch for the order sync. When off, no TD SYNNEX API calls are made; orders
              already mirrored stay visible.
            </p>
          </div>
          <Switch
            checked={enabled}
            disabled={toggleEnabled.isPending}
            onCheckedChange={(v) => toggleEnabled.mutate(v)}
            aria-label="Enable TD SYNNEX integration"
          />
        </div>
      </section>

      <section className="space-y-4 rounded-lg border border-glass bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Globe className="h-4 w-4 text-primary" /> Connection
        </div>
        <p className="text-xs text-muted-foreground">
          The API base URL from the TD SYNNEX API Credential center (production or sandbox). Only
          https addresses on a tdsynnex.com host are accepted. Leave the token URL empty to use{" "}
          <code>&lt;base URL&gt;/auth/token</code>.
        </p>
        {status.isLoading ? (
          <Skeleton className="h-20 w-full" />
        ) : (
          <div className="grid gap-4 sm:grid-cols-2">
            <label className="space-y-1.5">
              <span className="text-xs font-medium text-muted-foreground">Base URL</span>
              <Input
                placeholder="https://api.tdsynnex.com/eu"
                value={baseUrlDraft}
                onChange={(e) => setBaseUrlDraft(e.target.value)}
                spellCheck={false}
                autoComplete="off"
              />
            </label>
            <label className="space-y-1.5">
              <span className="text-xs font-medium text-muted-foreground">Token URL (optional)</span>
              <Input
                placeholder="Default: <base URL>/auth/token"
                value={tokenUrlDraft}
                onChange={(e) => setTokenUrlDraft(e.target.value)}
                spellCheck={false}
                autoComplete="off"
              />
            </label>
          </div>
        )}
        <div className="flex justify-end">
          <Button onClick={() => saveConnection.mutate()} disabled={!connectionDirty || saveConnection.isPending}>
            Save
          </Button>
        </div>
      </section>

      <section className="space-y-5 rounded-lg border border-glass bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Fingerprint className="h-4 w-4 text-primary" /> API credentials
        </div>
        <p className="text-xs text-muted-foreground">
          Client ID and client secret from the API Credential center (not your portal login). Both
          are encrypted at rest and never returned to the browser.
        </p>
        {status.isLoading ? (
          <Skeleton className="h-32 w-full" />
        ) : (
          <>
            <div className="grid gap-4 sm:grid-cols-2">
              <label className="space-y-1.5">
                <span className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <BadgeCheck className="h-3 w-3" /> Client ID
                </span>
                <Input
                  type="password"
                  placeholder={hasClientId ? "Configured — paste a new one to replace" : "Paste the client ID"}
                  value={clientIdDraft}
                  onChange={(e) => setClientIdDraft(e.target.value)}
                  spellCheck={false}
                  autoComplete="off"
                />
              </label>
              <label className="space-y-1.5">
                <span className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <KeyRound className="h-3 w-3" /> Client secret
                </span>
                <Input
                  type="password"
                  placeholder={hasSecret ? "Configured — paste a new one to replace" : "Paste the client secret"}
                  value={secretDraft}
                  onChange={(e) => setSecretDraft(e.target.value)}
                  spellCheck={false}
                  autoComplete="off"
                />
              </label>
            </div>
            <div className="flex items-center justify-between">
              {credentialsSet ? (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => clearCredentials.mutate()}
                  disabled={clearCredentials.isPending}
                  className="text-destructive hover:text-destructive"
                >
                  <Trash2 className="mr-1 h-3 w-3" /> Clear credentials
                </Button>
              ) : (
                <span />
              )}
              <div className="flex items-center gap-2">
                <Button variant="outline" onClick={() => test.mutate()} disabled={!connectionReady || test.isPending}>
                  {test.isPending ? (
                    <>
                      <RefreshCw className="mr-2 h-3 w-3 animate-spin" /> Testing…
                    </>
                  ) : (
                    <>
                      <CheckCircle2 className="mr-2 h-3 w-3" /> Test connection
                    </>
                  )}
                </Button>
                <Button
                  onClick={() => saveCredentials.mutate()}
                  disabled={
                    (clientIdDraft.trim().length === 0 && secretDraft.trim().length === 0) || saveCredentials.isPending
                  }
                >
                  Save
                </Button>
              </div>
            </div>
          </>
        )}
      </section>

      <section className="space-y-4 rounded-lg border border-glass bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Clock className="h-4 w-4 text-primary" /> Sync schedule
        </div>
        <p className="max-w-2xl text-xs text-muted-foreground">
          Every interval, orders changed since the previous pass are read. <em>History</em> sets how
          many days of orders (by order date) are kept in the list; <code>0</code> reads every order
          ever, completed ones included. Raising it later only reads the missing older orders. An order
          that is already in the list as completed is not read again — unless TD SYNNEX changes it
          (for example a late invoice). As a safety net, every order that is not yet completed is
          re-read in full at the <em>active refresh</em> interval.
        </p>
        <div className="flex flex-wrap items-end gap-3">
          <label className="space-y-1.5">
            <span className="text-[11px] font-medium text-muted-foreground">Interval (minutes, min 10)</span>
            <Input type="number" min={10} max={1440} value={intervalDraft} onChange={(e) => setIntervalDraft(e.target.value)} className="w-36" />
          </label>
          <label className="space-y-1.5">
            <span className="text-[11px] font-medium text-muted-foreground">History (days, 0 = all)</span>
            <Input type="number" min={0} max={3650} value={initialDaysDraft} onChange={(e) => setInitialDaysDraft(e.target.value)} className="w-36" />
          </label>
          <label className="space-y-1.5">
            <span className="text-[11px] font-medium text-muted-foreground">Active refresh (minutes, min 60)</span>
            <Input type="number" min={60} max={10080} value={activeDraft} onChange={(e) => setActiveDraft(e.target.value)} className="w-44" />
          </label>
          <Button onClick={() => saveSchedule.mutate()} disabled={!scheduleValid || !scheduleDirty || saveSchedule.isPending}>
            Save
          </Button>
        </div>
      </section>

      <section className="rounded-lg border border-glass bg-glass p-5">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="space-y-1">
            <h2 className="text-sm font-medium text-foreground">Order mirror</h2>
            <p className="text-xs text-muted-foreground">
              {data?.lastCheckedUtc ? (
                <>
                  {data.orderCount} orders mirrored
                  {lastSuccess && <span className="text-muted-foreground/70"> · last successful sync {lastSuccess}</span>}
                  {lastChecked && lastChecked !== lastSuccess && (
                    <span className="text-muted-foreground/70"> · checked {lastChecked}</span>
                  )}
                  <span className="text-muted-foreground/70">
                    {" "}
                    · last pass {data.lastSeen} read, {data.lastChanged} changed
                  </span>
                  <span className="text-muted-foreground/70">
                    {" "}
                    · history{" "}
                    {data.historyComplete
                      ? "complete (all orders)"
                      : data.historyFromUtc
                        ? `from ${formatDate(data.historyFromUtc, zone)}`
                        : "not read yet"}
                  </span>
                </>
              ) : (
                "No sync has run yet."
              )}
            </p>
            {data?.lastError && data.lastStatus !== "ok" && <p className="text-xs text-rose-300">{data.lastError}</p>}
          </div>
          <Button
            size="sm"
            variant="outline"
            className="gap-1.5"
            onClick={() => runSync.mutate()}
            disabled={runSync.isPending || !connectionReady}
          >
            <RefreshCw className={cn("h-3.5 w-3.5", runSync.isPending && "animate-spin")} />
            Sync now
          </Button>
        </div>
      </section>

      <section className="rounded-lg border border-glass bg-glass p-5">
        <h2 className="mb-3 text-sm font-medium text-foreground">Activity</h2>
        <IntegrationAuditLog integration="tdsynnex" />
      </section>
    </div>
  );
}
