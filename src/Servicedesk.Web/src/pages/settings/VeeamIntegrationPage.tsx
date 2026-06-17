import { useEffect, useMemo, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowLeft,
  Building2,
  CheckCircle2,
  Clock,
  DatabaseBackup,
  Globe,
  KeyRound,
  Network,
  RefreshCw,
  Search,
  ShieldAlert,
  Trash2,
  User,
  X,
} from "lucide-react";
import {
  ApiError,
  apiErrorMessage,
  veeamAdminApi,
  type VeeamConnectionState,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { IntegrationAuditLog } from "@/components/integrations/IntegrationAuditLog";
import { CompanyLinkPicker } from "@/components/integrations/CompanyLinkPicker";
import veeamLogo from "@/assets/integrations/veeam.svg";
import { cn } from "@/lib/utils";

const STATUS_QK = ["integrations", "veeam", "status"] as const;
const SECRET_QK = ["integrations", "veeam", "secret"] as const;
const COMPANIES_QK = ["integrations", "veeam", "companies"] as const;
const VSPC_QK = ["integrations", "veeam", "vspc-companies"] as const;
const LINKS_QK = ["integrations", "veeam", "links"] as const;
const LINKABLE_QK = ["integrations", "veeam", "linkable-companies"] as const;

function relativeStamp(iso: string | null | undefined): string {
  if (!iso) return "never";
  try {
    return new Intl.DateTimeFormat(undefined, {
      month: "short",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}

const STATE_LABEL: Record<
  VeeamConnectionState,
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

export function VeeamIntegrationPage() {
  const qc = useQueryClient();
  const status = useQuery({ queryKey: STATUS_QK, queryFn: veeamAdminApi.status });
  const secret = useQuery({ queryKey: SECRET_QK, queryFn: veeamAdminApi.secretStatus });

  const [urlDraft, setUrlDraft] = useState("");
  const [userDraft, setUserDraft] = useState("");
  const [selfSignedDraft, setSelfSignedDraft] = useState(false);
  const [passwordDraft, setPasswordDraft] = useState("");
  const [intervalDraft, setIntervalDraft] = useState("");
  const [seeded, setSeeded] = useState(false);

  // Seed the editable fields once from the server, so an admin edits in place.
  useEffect(() => {
    if (!seeded && status.data) {
      setUrlDraft(status.data.baseUrl ?? "");
      setUserDraft(status.data.username ?? "");
      setSelfSignedDraft(status.data.allowSelfSignedTls ?? false);
      setIntervalDraft(String(status.data.syncIntervalMinutes ?? 360));
      setSeeded(true);
    }
  }, [seeded, status.data]);

  const hasSecret = secret.data?.configured ?? false;
  const enabled = status.data?.enabled ?? false;
  const state = status.data?.state ?? "disabled";
  const stateMeta = STATE_LABEL[state];

  const onErr = (err: unknown) => {
    const upstream = apiErrorMessage(err);
    toast.error(
      upstream ?? (err instanceof ApiError ? `Failed (${err.status})` : "Failed"),
    );
  };

  const saveConfig = useMutation({
    mutationFn: () =>
      veeamAdminApi.setConfig(urlDraft.trim(), userDraft.trim(), selfSignedDraft),
    onSuccess: () => {
      toast.success("Connection settings saved");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const saveSecret = useMutation({
    mutationFn: () => veeamAdminApi.setSecret(passwordDraft),
    onSuccess: () => {
      toast.success("Password saved");
      setPasswordDraft("");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const deleteSecret = useMutation({
    mutationFn: () => veeamAdminApi.deleteSecret(),
    onSuccess: () => {
      toast.success("Password cleared");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const toggleEnabled = useMutation({
    mutationFn: (next: boolean) => veeamAdminApi.setEnabled(next),
    onSuccess: (_d, next) => {
      toast.success(next ? "Integration enabled" : "Integration disabled");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const saveInterval = useMutation({
    mutationFn: () => veeamAdminApi.setSyncInterval(Number(intervalDraft)),
    onSuccess: (r) => {
      toast.success(`Sync interval set to ${r.syncIntervalMinutes} minutes`);
      setIntervalDraft(String(r.syncIntervalMinutes));
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: onErr,
  });

  const test = useMutation({
    mutationFn: () => veeamAdminApi.testConnection(),
    onSuccess: (result) => {
      if (result.success) {
        const seen =
          typeof result.companyCount === "number"
            ? ` — ${result.companyCount} companies visible`
            : "";
        toast.success(`Connection valid${seen} (${result.latencyMs} ms)`);
      } else {
        toast.error(result.message ?? "Test failed");
      }
    },
    onError: onErr,
  });

  const sync = useMutation({
    mutationFn: () => veeamAdminApi.sync(),
    onSuccess: (r) => {
      if (r.success) {
        toast.success(
          r.changed
            ? `Synced — ${r.companyCount} companies, ${r.objectCount} backup objects`
            : "Synced — no changes",
        );
      } else {
        toast.error(r.error ?? "Sync failed");
      }
      qc.invalidateQueries({ queryKey: STATUS_QK });
      qc.invalidateQueries({ queryKey: COMPANIES_QK });
    },
    onError: onErr,
  });

  const companies = useQuery({
    queryKey: COMPANIES_QK,
    queryFn: veeamAdminApi.companies,
    enabled: state === "ready",
  });

  const vspcCompanies = useQuery({
    queryKey: VSPC_QK,
    queryFn: veeamAdminApi.vspcCompanies,
    enabled: state === "ready",
  });
  const links = useQuery({
    queryKey: LINKS_QK,
    queryFn: veeamAdminApi.links,
    enabled: state === "ready",
  });
  const linkable = useQuery({
    queryKey: LINKABLE_QK,
    queryFn: veeamAdminApi.linkableCompanies,
    enabled: state === "ready",
  });

  const [vspcFilter, setVspcFilter] = useState("");

  const addLink = useMutation({
    mutationFn: (vars: { parentCompanyId: string; vspcCompanyUid: string }) =>
      veeamAdminApi.addLink(vars.parentCompanyId, vars.vspcCompanyUid),
    onSuccess: () => {
      toast.success("VSPC company linked — its backups roll into that company on the next sync");
      qc.invalidateQueries({ queryKey: LINKS_QK });
    },
    onError: onErr,
  });
  const removeLink = useMutation({
    mutationFn: (vars: { parentCompanyId: string; vspcCompanyUid: string }) =>
      veeamAdminApi.removeLink(vars.parentCompanyId, vars.vspcCompanyUid),
    onSuccess: () => {
      toast.success("VSPC company link removed");
      qc.invalidateQueries({ queryKey: LINKS_QK });
    },
    onError: onErr,
  });

  const linkableCompanies = linkable.data?.items ?? [];

  // Parent links grouped per VSPC company uid, for the rollup table.
  const linksByUid = useMemo(() => {
    const map = new Map<string, { parentCompanyId: string; parentCompanyName: string | null }[]>();
    for (const l of links.data?.items ?? []) {
      const list = map.get(l.vspcCompanyUid) ?? [];
      list.push({ parentCompanyId: l.parentCompanyId, parentCompanyName: l.parentCompanyName });
      map.set(l.vspcCompanyUid, list);
    }
    return map;
  }, [links.data]);

  const filteredVspc = useMemo(() => {
    const needle = vspcFilter.trim().toLowerCase();
    const items = vspcCompanies.data?.items ?? [];
    if (needle.length === 0) return items;
    return items.filter(
      (v) =>
        (v.name ?? "").toLowerCase().includes(needle) ||
        (v.code ?? "").toLowerCase().includes(needle),
    );
  }, [vspcCompanies.data, vspcFilter]);

  const urlValid = /^https?:\/\/.+/i.test(urlDraft.trim());
  const configDirty =
    urlDraft.trim() !== (status.data?.baseUrl ?? "") ||
    userDraft.trim() !== (status.data?.username ?? "") ||
    selfSignedDraft !== (status.data?.allowSelfSignedTls ?? false);
  const canSaveConfig =
    urlValid &&
    userDraft.trim().length > 0 &&
    configDirty &&
    !saveConfig.isPending;
  const canTest =
    (status.data?.baseUrl?.length ?? 0) > 0 &&
    (status.data?.username?.length ?? 0) > 0 &&
    hasSecret &&
    !test.isPending;

  const intervalNum = Number(intervalDraft);
  const canSaveInterval =
    Number.isFinite(intervalNum) &&
    intervalNum >= 60 &&
    intervalNum <= 10080 &&
    intervalNum !== (status.data?.syncIntervalMinutes ?? 360) &&
    !saveInterval.isPending;

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
            <img src={veeamLogo} alt="" aria-hidden className="h-5 w-5" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Veeam</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            Connect your <strong>Veeam Service Provider Console</strong> (VSPC) so the app can
            see, per customer, which Microsoft 365 mailboxes are backed up (Exchange and
            OneDrive). Enter your console's API URL and a login below. The matching surface
            that uses this data lives under <strong>Contracts</strong>.
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
              Master switch for the Veeam reader. When off, no Veeam API calls are made even
              when the URL and login are configured.
            </p>
          </div>
          <Switch
            checked={enabled}
            disabled={toggleEnabled.isPending}
            onCheckedChange={(v) => toggleEnabled.mutate(v)}
            aria-label="Enable Veeam integration"
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
              How often the Veeam backup data is re-read. Minimum 60 minutes.
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

      {/* Connection */}
      <section className="rounded-lg border border-glass bg-glass p-5 space-y-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Globe className="h-4 w-4 text-primary" /> VSPC connection
        </div>
        <p className="-mt-2 text-xs text-muted-foreground">
          The REST API URL and login of your Veeam Service Provider Console. The URL and
          username are stored as settings; the password is encrypted at rest via DataProtection
          and never returned to the browser.
        </p>

        {status.isLoading ? (
          <Skeleton className="h-32 w-full" />
        ) : (
          <>
            <div className="grid gap-4 sm:grid-cols-2">
              <label className="space-y-1.5">
                <span className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <Globe className="h-3 w-3" /> API base URL
                </span>
                <Input
                  placeholder="https://your-vspc-host:1280"
                  value={urlDraft}
                  onChange={(e) => setUrlDraft(e.target.value)}
                  spellCheck={false}
                  autoComplete="off"
                />
                <span className="text-[11px] text-muted-foreground/70">
                  Include the port (VSPC default is <code>1280</code>). No trailing path.
                </span>
              </label>
              <label className="space-y-1.5">
                <span className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <User className="h-3 w-3" /> Username
                </span>
                <Input
                  placeholder="VSPC API account"
                  value={userDraft}
                  onChange={(e) => setUserDraft(e.target.value)}
                  spellCheck={false}
                  autoComplete="off"
                />
                <span className="text-[11px] text-muted-foreground/70">
                  The account used for the API (OAuth2 password grant).
                </span>
              </label>
            </div>

            <label className="flex items-start justify-between gap-4 rounded-md border border-glass-strong bg-black/10 px-3 py-2.5">
              <span className="space-y-0.5">
                <span className="flex items-center gap-1.5 text-xs font-medium text-foreground">
                  <ShieldAlert className="h-3 w-3 text-amber-400" /> Accept self-signed certificate
                </span>
                <span className="block text-[11px] text-muted-foreground/70">
                  VSPC appliances commonly ship a self-signed TLS certificate. Enable only if
                  you trust the network path to the console.
                </span>
              </span>
              <Switch
                checked={selfSignedDraft}
                onCheckedChange={setSelfSignedDraft}
                aria-label="Accept self-signed certificate"
              />
            </label>

            <div className="flex justify-end">
              <Button onClick={() => saveConfig.mutate()} disabled={!canSaveConfig}>
                Save connection
              </Button>
            </div>

            <div className="border-t border-glass pt-5">
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                  <KeyRound className="h-3 w-3" /> Password
                </span>
                {hasSecret && (
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => deleteSecret.mutate()}
                    disabled={deleteSecret.isPending}
                    className="text-destructive hover:text-destructive"
                  >
                    <Trash2 className="mr-1 h-3 w-3" /> Clear
                  </Button>
                )}
              </div>
              <div className="mt-1.5 flex gap-2">
                <Input
                  type="password"
                  placeholder={
                    hasSecret
                      ? "Password configured — type a new one to replace"
                      : "Password for the API account"
                  }
                  value={passwordDraft}
                  onChange={(e) => setPasswordDraft(e.target.value)}
                  spellCheck={false}
                  autoComplete="off"
                />
                <Button
                  onClick={() => saveSecret.mutate()}
                  disabled={passwordDraft.trim().length === 0 || saveSecret.isPending}
                >
                  Save
                </Button>
              </div>
            </div>

            <div className="flex items-center justify-between border-t border-glass pt-5">
              <p className="text-xs text-muted-foreground">
                Verify the connection by requesting an access token from the console.
              </p>
              <Button variant="outline" onClick={() => test.mutate()} disabled={!canTest}>
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
            </div>
          </>
        )}
      </section>

      {/* Backup data */}
      <section className="rounded-lg border border-glass bg-glass p-5 space-y-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="space-y-1">
            <div className="flex items-center gap-2 text-sm font-medium text-foreground">
              <DatabaseBackup className="h-4 w-4 text-primary" /> Backup data
            </div>
            <p className="max-w-xl text-xs text-muted-foreground">
              Reads the VB365 protected objects for every Microsoft 365-connected company that
              matches a VSPC company by relation code. The per-mailbox OneDrive / Exchange
              backup status appears on each company under{" "}
              <strong>Contracts → Microsoft 365 matching</strong>.
            </p>
          </div>
          <Button
            variant="outline"
            className="gap-1.5"
            onClick={() => sync.mutate()}
            disabled={state !== "ready" || sync.isPending}
          >
            <RefreshCw className={cn("h-3.5 w-3.5", sync.isPending && "animate-spin")} />
            Sync now
          </Button>
        </div>

        {state === "ready" && (
          <div className="flex flex-wrap gap-x-6 gap-y-1 text-[11px] text-muted-foreground">
            <span>
              Last checked:{" "}
              <span className="text-foreground">{relativeStamp(status.data?.lastCheckedUtc)}</span>
            </span>
            <span>
              Last change:{" "}
              <span className="text-foreground">{relativeStamp(status.data?.lastChangedUtc)}</span>
            </span>
            <span>
              Matched companies:{" "}
              <span className="tabular-nums text-foreground">{status.data?.companyCount ?? 0}</span>
            </span>
            <span>
              Backup objects:{" "}
              <span className="tabular-nums text-foreground">{status.data?.objectCount ?? 0}</span>
            </span>
            {status.data?.lastStatus && status.data.lastStatus !== "ok" && (
              <span className="text-amber-300">
                {status.data.lastError ?? status.data.lastStatus}
              </span>
            )}
          </div>
        )}

        {state === "ready" && (companies.data?.items.length ?? 0) > 0 && (
          <div className="overflow-hidden rounded-md border border-glass-strong">
            <table className="w-full text-xs">
              <thead className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
                <tr className="border-b border-glass">
                  <th className="px-3 py-2 text-left">Company</th>
                  <th className="px-3 py-2 text-left">Code</th>
                  <th className="px-3 py-2 text-left">VSPC name</th>
                  <th className="px-3 py-2 text-right">Objects</th>
                  <th className="px-3 py-2 text-right">Synced</th>
                </tr>
              </thead>
              <tbody>
                {companies.data!.items.map((c) => (
                  <tr key={c.companyId} className="border-b border-glass last:border-b-0">
                    <td className="px-3 py-2 text-foreground">
                      <span className="inline-flex items-center gap-1.5">
                        <Building2 className="h-3 w-3 text-muted-foreground/60" />
                        {c.companyName ?? "—"}
                      </span>
                    </td>
                    <td className="px-3 py-2 font-mono text-muted-foreground">{c.companyCode ?? "—"}</td>
                    <td className="px-3 py-2 text-muted-foreground">{c.vspcCompanyName ?? "—"}</td>
                    <td className="px-3 py-2 text-right tabular-nums text-muted-foreground">{c.objectCount}</td>
                    <td className="px-3 py-2 text-right text-muted-foreground">{relativeStamp(c.lastSyncedUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {/* Rollup links */}
      {state === "ready" && (
        <section className="rounded-lg border border-glass bg-glass p-5 space-y-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="space-y-1">
              <div className="flex items-center gap-2 text-sm font-medium text-foreground">
                <Network className="h-4 w-4 text-primary" /> Rollup links
              </div>
              <p className="max-w-2xl text-xs text-muted-foreground">
                Each VSPC company is matched to a Servicedesk company automatically by relation
                code. When a customer runs several companies under one Microsoft 365 tenant but a
                separate Veeam (VSPC) company per company, attach the extra VSPC companies to the
                parent — their backups then merge into that company's Microsoft 365 matching view on
                the next sync, on top of the automatic match.
              </p>
            </div>
            <div className="flex items-center gap-2 rounded-md border border-glass-strong bg-black/10 px-2.5 py-1.5">
              <Search className="h-3.5 w-3.5 text-muted-foreground" />
              <input
                value={vspcFilter}
                onChange={(e) => setVspcFilter(e.target.value)}
                placeholder="Filter VSPC companies…"
                spellCheck={false}
                className="w-44 bg-transparent text-xs text-foreground outline-none placeholder:text-muted-foreground/60"
              />
            </div>
          </div>

          {vspcCompanies.isLoading ? (
            <div className="space-y-2">
              <Skeleton className="h-9 w-full" />
              <Skeleton className="h-9 w-full" />
              <Skeleton className="h-9 w-full" />
            </div>
          ) : (vspcCompanies.data?.items.length ?? 0) === 0 ? (
            <p className="text-xs text-muted-foreground">
              No VSPC companies yet. Run a sync to load the company list.
            </p>
          ) : (
            <div className="overflow-x-auto rounded-md border border-glass-strong">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                    <th className="px-3 py-2.5 font-medium">VSPC company</th>
                    <th className="px-3 py-2.5 font-medium">Code</th>
                    <th className="px-3 py-2.5 font-medium">Auto-match</th>
                    <th className="px-3 py-2.5 font-medium">Rollup into</th>
                  </tr>
                </thead>
                <tbody>
                  {filteredVspc.map((v) => {
                    const rowLinks = linksByUid.get(v.vspcCompanyUid) ?? [];
                    const excludeIds = new Set<string>(rowLinks.map((l) => l.parentCompanyId));
                    return (
                      <tr key={v.vspcCompanyUid} className="border-b border-glass last:border-0">
                        <td className="px-3 py-2.5 text-foreground">{v.name ?? "—"}</td>
                        <td className="px-3 py-2.5 font-mono text-xs text-muted-foreground">
                          {v.code ?? "—"}
                        </td>
                        <td className="px-3 py-2.5">
                          {v.matchedCompanyName ? (
                            <span className="text-foreground">{v.matchedCompanyName}</span>
                          ) : (
                            <span className="text-muted-foreground/60">Unmatched</span>
                          )}
                        </td>
                        <td className="px-3 py-2.5">
                          <div className="flex flex-wrap items-center gap-1.5">
                            {rowLinks.map((l) => (
                              <span
                                key={l.parentCompanyId}
                                className="inline-flex items-center gap-1 rounded-full border border-glass bg-glass px-2 py-0.5 text-[11px] text-foreground"
                              >
                                {l.parentCompanyName ?? "—"}
                                <button
                                  type="button"
                                  aria-label="Remove rollup link"
                                  disabled={removeLink.isPending}
                                  onClick={() =>
                                    removeLink.mutate({
                                      parentCompanyId: l.parentCompanyId,
                                      vspcCompanyUid: v.vspcCompanyUid,
                                    })
                                  }
                                  className="text-muted-foreground/70 transition-colors hover:text-rose-300 disabled:opacity-50"
                                >
                                  <X className="h-3 w-3" />
                                </button>
                              </span>
                            ))}
                            <CompanyLinkPicker
                              companies={linkableCompanies}
                              excludeIds={excludeIds}
                              disabled={addLink.isPending}
                              label={rowLinks.length > 0 ? "Add" : "Link company"}
                              onSelect={(parentCompanyId) =>
                                addLink.mutate({
                                  parentCompanyId,
                                  vspcCompanyUid: v.vspcCompanyUid,
                                })
                              }
                            />
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}

      {/* Audit log */}
      <section className="rounded-lg border border-glass bg-glass p-5">
        <h2 className="mb-3 text-sm font-medium text-foreground">Integration audit log</h2>
        <IntegrationAuditLog integration="veeam" />
      </section>
    </div>
  );
}
