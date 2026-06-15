import { useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowLeft,
  BadgeCheck,
  CheckCircle2,
  Clock,
  Fingerprint,
  KeyRound,
  RefreshCw,
  Trash2,
} from "lucide-react";
import {
  ApiError,
  apiErrorMessage,
  sophosAdminApi,
  type SophosConnectionState,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { IntegrationAuditLog } from "@/components/integrations/IntegrationAuditLog";
import sophosLogo from "@/assets/integrations/sophos.svg";
import { cn } from "@/lib/utils";

const STATUS_QK = ["integrations", "sophos", "status"] as const;
const TENANTS_QK = ["integrations", "sophos", "tenants"] as const;

const STATE_LABEL: Record<
  SophosConnectionState,
  { tone: string; text: string; dot: string }
> = {
  disabled: {
    tone: "border-glass-strong bg-glass-strong text-muted-foreground",
    text: "Disabled",
    dot: "bg-glass-strong",
  },
  "not-configured": {
    tone: "border-amber-400/30 bg-amber-500/[0.08] text-amber-200",
    text: "Not configured",
    dot: "bg-amber-400",
  },
  ready: {
    tone: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    text: "Ready",
    dot: "bg-emerald-400",
  },
};

/** "5m ago" relative copy from a server timestamp. */
function relativeFromUtc(iso: string | null | undefined): string | null {
  if (!iso) return null;
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return null;
  const mins = Math.round((Date.now() - then) / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

export function SophosIntegrationPage() {
  const qc = useQueryClient();
  const status = useQuery({ queryKey: STATUS_QK, queryFn: sophosAdminApi.status });
  const tenants = useQuery({ queryKey: TENANTS_QK, queryFn: sophosAdminApi.tenants });

  const [clientIdDraft, setClientIdDraft] = useState("");
  const [secretDraft, setSecretDraft] = useState("");
  const [intervalDraft, setIntervalDraft] = useState("");
  const [seeded, setSeeded] = useState(false);

  useEffect(() => {
    if (!seeded && status.data) {
      setIntervalDraft(String(status.data.syncIntervalMinutes ?? 360));
      setSeeded(true);
    }
  }, [seeded, status.data]);

  const enabled = status.data?.enabled ?? false;
  const state = status.data?.state ?? "disabled";
  const stateMeta = STATE_LABEL[state];
  const hasClientId = status.data?.clientIdConfigured ?? false;
  const hasSecret = status.data?.clientSecretConfigured ?? false;
  const configured = hasClientId && hasSecret;

  const onErr = (err: unknown) => {
    const upstream = apiErrorMessage(err);
    toast.error(upstream ?? (err instanceof ApiError ? `Failed (${err.status})` : "Failed"));
  };

  const saveCredentials = useMutation({
    mutationFn: () =>
      sophosAdminApi.setCredentials(
        clientIdDraft.trim() || null,
        secretDraft.trim() || null,
      ),
    onSuccess: () => {
      toast.success("Sophos credentials saved");
      setClientIdDraft("");
      setSecretDraft("");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const clearCredentials = useMutation({
    mutationFn: () => sophosAdminApi.deleteCredentials(),
    onSuccess: () => {
      toast.success("Sophos credentials cleared");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const toggleEnabled = useMutation({
    mutationFn: (next: boolean) => sophosAdminApi.setEnabled(next),
    onSuccess: (_d, next) => {
      toast.success(next ? "Integration enabled" : "Integration disabled");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const saveInterval = useMutation({
    mutationFn: () => sophosAdminApi.setSyncInterval(Number(intervalDraft)),
    onSuccess: (r) => {
      toast.success(`Sync interval set to ${r.syncIntervalMinutes} minutes`);
      setIntervalDraft(String(r.syncIntervalMinutes));
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const test = useMutation({
    mutationFn: () => sophosAdminApi.testConnection(),
    onSuccess: (result) => {
      if (result.success) {
        toast.success(`Credentials valid — ${result.tenantCount ?? 0} tenants visible`);
      } else {
        toast.error(result.message ?? "Test failed");
      }
    },
    onError: onErr,
  });

  const runSync = useMutation({
    mutationFn: () => sophosAdminApi.sync(),
    onSuccess: (r) => {
      if (r.success) {
        toast.success(
          `Synced — ${r.tenantCount} tenants, ${r.mailboxCount} protected mailboxes${
            r.changed ? " (updated)" : " (no changes)"
          }`,
        );
      } else if (r.status === "busy") {
        toast.message("A sync is already running.");
      } else {
        toast.error(r.error ?? "Sync failed");
      }
      qc.invalidateQueries({ queryKey: STATUS_QK });
      qc.invalidateQueries({ queryKey: TENANTS_QK });
    },
    onError: onErr,
  });

  const intervalNum = Number(intervalDraft);
  const canSaveInterval =
    Number.isFinite(intervalNum) &&
    intervalNum >= 60 &&
    intervalNum <= 10080 &&
    intervalNum !== (status.data?.syncIntervalMinutes ?? 360) &&
    !saveInterval.isPending;

  const lastChecked = relativeFromUtc(status.data?.lastCheckedUtc);
  const lastChanged = relativeFromUtc(status.data?.lastChangedUtc);

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
          <div className="mt-2 mb-2 flex h-10 w-10 items-center justify-center rounded-lg border border-glass-strong bg-glass">
            <img src={sophosLogo} alt="" aria-hidden className="h-5 w-5" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Sophos</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            One Sophos Central <strong>partner</strong> API credential lists every tenant you
            manage. Each tenant's name carries the customer code as <code>[NNN]</code>, which
            matches a Servicedesk company. For companies connected with Microsoft 365, the
            tenant's protected mailboxes are pulled and each mailbox is marked{" "}
            <strong>Protected</strong> / <strong>Unprotected</strong> under{" "}
            <strong>Contracts → Microsoft 365 matching</strong>.
          </p>
        </div>
        <Badge className={cn("border text-xs font-normal", stateMeta.tone)}>
          <span className={cn("mr-1.5 inline-block h-1.5 w-1.5 rounded-full", stateMeta.dot)} />
          {stateMeta.text}
        </Badge>
      </header>

      {/* Enabled toggle */}
      <section className="rounded-lg border border-glass bg-glass p-5">
        <div className="flex items-center justify-between gap-4">
          <div>
            <h2 className="text-sm font-medium text-foreground">Enabled</h2>
            <p className="text-xs text-muted-foreground">
              Master switch for the Sophos sync. When off, no Sophos API calls are made and the
              Microsoft 365 matching view shows no spam-filter column.
            </p>
          </div>
          <Switch
            checked={enabled}
            disabled={toggleEnabled.isPending}
            onCheckedChange={(v) => toggleEnabled.mutate(v)}
            aria-label="Enable Sophos integration"
          />
        </div>
      </section>

      {/* Sync schedule */}
      <section className="rounded-lg border border-glass bg-glass p-5">
        <div className="flex flex-wrap items-end justify-between gap-4">
          <div className="space-y-1">
            <div className="flex items-center gap-2 text-sm font-medium text-foreground">
              <Clock className="h-4 w-4 text-primary" /> Sync schedule
            </div>
            <p className="max-w-xl text-xs text-muted-foreground">
              How often the partner tenant list and the protected mailboxes of Microsoft
              365-connected tenants are re-read. Unchanged snapshots skip the rewrite, so a
              shorter interval is inexpensive. Minimum 60 minutes.
            </p>
          </div>
          <div className="flex items-end gap-2">
            <label className="space-y-1.5">
              <span className="text-[11px] font-medium text-muted-foreground">Interval (minutes)</span>
              <Input
                type="number"
                min={60}
                max={10080}
                step={30}
                value={intervalDraft}
                onChange={(e) => setIntervalDraft(e.target.value)}
                className="w-32"
              />
            </label>
            <Button onClick={() => saveInterval.mutate()} disabled={!canSaveInterval}>
              Save
            </Button>
          </div>
        </div>
      </section>

      {/* API credentials */}
      <section className="rounded-lg border border-glass bg-glass p-5 space-y-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Fingerprint className="h-4 w-4 text-primary" /> API credentials
        </div>
        <p className="-mt-2 text-xs text-muted-foreground">
          The partner API client ID and secret from the Sophos Central partner dashboard. Both
          are encrypted at rest via DataProtection and never returned to the browser.
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
              {configured ? (
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
                <Button
                  variant="outline"
                  onClick={() => test.mutate()}
                  disabled={!configured || test.isPending}
                >
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
                    (clientIdDraft.trim().length === 0 && secretDraft.trim().length === 0) ||
                    saveCredentials.isPending
                  }
                >
                  Save
                </Button>
              </div>
            </div>
          </>
        )}
      </section>

      {/* Sync status */}
      <section className="rounded-lg border border-glass bg-glass p-5">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="space-y-1">
            <h2 className="text-sm font-medium text-foreground">Tenant snapshot</h2>
            <p className="text-xs text-muted-foreground">
              {status.data ? (
                <>
                  {status.data.tenantCount} tenants · {status.data.mailboxCount} protected
                  mailboxes
                  {lastChecked && <span className="text-muted-foreground/70"> · checked {lastChecked}</span>}
                  {lastChanged && lastChanged !== lastChecked && (
                    <span className="text-muted-foreground/70"> · changed {lastChanged}</span>
                  )}
                </>
              ) : (
                "No sync has run yet."
              )}
            </p>
            {status.data?.lastError && status.data.lastStatus !== "ok" && (
              <p className="text-xs text-rose-300">{status.data.lastError}</p>
            )}
          </div>
          <Button
            size="sm"
            variant="outline"
            className="gap-1.5"
            onClick={() => runSync.mutate()}
            disabled={runSync.isPending}
          >
            <RefreshCw className={cn("h-3.5 w-3.5", runSync.isPending && "animate-spin")} />
            Sync now
          </Button>
        </div>
      </section>

      {/* Tenant list */}
      <section className="rounded-lg border border-glass bg-glass p-5">
        <h2 className="mb-3 text-sm font-medium text-foreground">Tenants</h2>
        {tenants.isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-9 w-full" />
          </div>
        ) : (tenants.data?.items.length ?? 0) === 0 ? (
          <p className="text-xs text-muted-foreground">
            No tenants yet. Configure the credentials, enable the integration and run a sync.
          </p>
        ) : (
          <div className="overflow-x-auto rounded-md border border-glass-strong">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                  <th className="px-3 py-2.5 font-medium">Sophos tenant</th>
                  <th className="px-3 py-2.5 font-medium">Code</th>
                  <th className="px-3 py-2.5 font-medium">Matched company</th>
                  <th className="px-3 py-2.5 font-medium">M365</th>
                  <th className="px-3 py-2.5 font-medium text-right">Protected</th>
                </tr>
              </thead>
              <tbody>
                {tenants.data?.items.map((t) => (
                  <tr key={t.tenantId} className="border-b border-glass last:border-0">
                    <td className="px-3 py-2.5 text-foreground">{t.showAs || t.name || "—"}</td>
                    <td className="px-3 py-2.5 font-mono text-xs text-muted-foreground">
                      {t.companyCode ?? "—"}
                    </td>
                    <td className="px-3 py-2.5">
                      {t.companyName ? (
                        <span className="text-foreground">{t.companyName}</span>
                      ) : (
                        <span className="text-muted-foreground/60">Unmatched</span>
                      )}
                    </td>
                    <td className="px-3 py-2.5">
                      {t.m365Connected ? (
                        <span className="inline-flex items-center gap-1 text-xs text-emerald-300">
                          <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" /> Connected
                        </span>
                      ) : (
                        <span className="text-xs text-muted-foreground/50">—</span>
                      )}
                    </td>
                    <td className="px-3 py-2.5 text-right tabular-nums text-muted-foreground">
                      {t.m365Connected ? t.mailboxCount : "—"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {/* Audit log */}
      <section className="rounded-lg border border-glass bg-glass p-5">
        <h2 className="mb-3 text-sm font-medium text-foreground">Integration audit log</h2>
        <IntegrationAuditLog integration="sophos" />
      </section>
    </div>
  );
}
