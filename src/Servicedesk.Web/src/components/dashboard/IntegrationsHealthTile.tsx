import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { toast } from "sonner";
import {
  ArrowRight,
  CheckCircle2,
  AlertTriangle,
  Plug,
  RefreshCw,
  Receipt,
  Package,
} from "lucide-react";
import {
  ApiError,
  adsolutApi,
  healthApi,
  integrationsHealthApi,
  type HealthAction,
  type HealthStatus,
  type IntegrationHealth,
  type SubsystemHealth,
  type AdsolutErpSalesReceiptsState,
  type AdsolutErpOrdersState,
} from "@/lib/api";
import { pollIntervalMs } from "@/lib/healthPolling";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import adsolutLogo from "@/assets/integrations/adsolut.ico";
import microsoftLogo from "@/assets/integrations/microsoft.svg";
import sophosLogo from "@/assets/integrations/sophos.svg";
import telavoxLogo from "@/assets/integrations/telavox.svg";
import veeamLogo from "@/assets/integrations/veeam.svg";
import zammadLogo from "@/assets/integrations/zammad.svg";

const QUERY_KEY = ["admin", "integrations-health"] as const;

const STATUS_STYLES: Record<
  HealthStatus,
  { badge: string; dot: string; label: string; icon: React.ReactNode }
> = {
  Ok: {
    badge:
      "border-emerald-400/60 bg-emerald-100 text-emerald-800 " +
      "dark:border-emerald-400/30 dark:bg-emerald-500/10 dark:text-emerald-300",
    dot: "bg-emerald-500 dark:bg-emerald-400",
    label: "OK",
    icon: <CheckCircle2 className="h-3.5 w-3.5" />,
  },
  Warning: {
    badge:
      "border-amber-400/60 bg-amber-100 text-amber-800 " +
      "dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-300",
    dot: "bg-amber-500 dark:bg-amber-400",
    label: "Warning",
    icon: <AlertTriangle className="h-3.5 w-3.5" />,
  },
  Critical: {
    badge:
      "border-rose-400/60 bg-rose-100 text-rose-800 " +
      "dark:border-rose-400/40 dark:bg-rose-500/10 dark:text-rose-300",
    dot: "bg-rose-500 dark:bg-rose-400",
    label: "Critical",
    icon: <AlertTriangle className="h-3.5 w-3.5" />,
  },
};

// Backend ships a stable `logoKey` per integration; the SPA holds the actual
// asset URLs. New connectors register here when their tile lands.
const LOGOS: Record<string, string> = {
  adsolut: adsolutLogo,
  telavox: telavoxLogo,
  zammad: zammadLogo,
  m365: microsoftLogo,
  sophos: sophosLogo,
  veeam: veeamLogo,
};

const DETAIL_ROUTES: Record<string, string> = {
  adsolut: "/settings/integrations/adsolut",
  telavox: "/settings/integrations/telavox",
  zammad: "/settings/integrations/zammad",
  m365: "/settings/integrations/m365",
  sophos: "/settings/integrations/sophos",
  veeam: "/settings/integrations/veeam",
};

/// Admin-only dashboard tile. Shows a compact roll-up of every configured
/// integration with its Connection + Sync checks side by side. Renders
/// nothing when the backend reports an empty integrations list, so a vanilla
/// install does not show an empty card.
export function IntegrationsHealthTile() {
  const query = useQuery({
    queryKey: QUERY_KEY,
    queryFn: () => integrationsHealthApi.get(),
    refetchInterval: (q) => pollIntervalMs(q.state.data),
  });

  if (query.isLoading) {
    return <Skeleton className="h-24 w-full" />;
  }

  const integrations = query.data?.integrations ?? [];
  if (integrations.length === 0) {
    // Either query failed or no integration configured. Either way the
    // tile is intentionally invisible — the System health card carries the
    // platform-level signal, and an empty integrations card would be noise.
    return null;
  }

  const rollup = query.data?.status ?? "Ok";
  const style = STATUS_STYLES[rollup];

  return (
    <section className="glass-card flex h-full flex-col p-5">
      <header className="mb-4 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Plug className="h-4 w-4 text-primary" />
          Integrations
        </div>
        <Badge className={`border text-[11px] font-normal ${style.badge}`}>
          <span className="mr-1">{style.icon}</span>
          {style.label}
        </Badge>
      </header>

      {/* One self-contained sub-card per integration. Grid breakpoints chosen
          so a typical install (2-3 integrations) lays out as one row on xl
          without forcing single-column on a tablet; a future fourth integration
          wraps to the next row cleanly. items-stretch (grid default) keeps the
          cards in the same row visually balanced even when one carries an
          extra strip (e.g. Adsolut's coverage row). */}
      <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-3">
        {integrations.map((i) => (
          <IntegrationCard key={i.key} integration={i} />
        ))}
        {/* v0.0.60 — the two opt-in Adsolut ERP read-mirrors get their own
            cards next to the main companies/contacts integration. Each renders
            nothing until its mirror is enabled, so a plain Adsolut install only
            ever shows the one card. */}
        <AdsolutErpSyncCard />
        <AdsolutOrdersSyncCard />
      </div>
    </section>
  );
}

/// Self-contained integration sub-card. Layout — header strip (logo + name +
/// per-integration status), checks stacked, optional extras (Adsolut coverage
/// chips), then the tile-level actions footer pinned to the bottom via
/// `mt-auto` so cards in the same grid row keep their action buttons
/// horizontally aligned even when their bodies have unequal height.
function IntegrationCard({ integration }: { integration: IntegrationHealth }) {
  const navigate = useNavigate();
  const logoSrc = LOGOS[integration.logoKey];
  const detailRoute = DETAIL_ROUTES[integration.key];
  const rollupStyle = STATUS_STYLES[integration.status];

  const handleHeaderClick = () => {
    if (detailRoute) void navigate({ to: detailRoute });
  };

  return (
    <article className="group flex flex-col rounded-lg border border-glass-strong bg-glass p-4 transition-colors hover:border-glass-strong hover:bg-glass-hover">
      <header className="mb-3 flex items-center justify-between gap-3">
        <button
          type="button"
          onClick={handleHeaderClick}
          disabled={!detailRoute}
          className={cn(
            "-m-1 flex items-center gap-2.5 rounded-md p-1 text-left transition-colors",
            detailRoute && "hover:bg-glass-hover",
            !detailRoute && "cursor-default",
          )}
        >
          {logoSrc ? (
            <img
              src={logoSrc}
              alt=""
              aria-hidden="true"
              draggable={false}
              className="h-8 w-8 select-none rounded-sm object-contain"
            />
          ) : (
            <div className="h-8 w-8 rounded-sm border border-glass bg-glass" />
          )}
          <div className="min-w-0">
            <div className="text-sm font-semibold text-foreground">{integration.name}</div>
            {detailRoute ? (
              <div className="flex items-center gap-1 text-[11px] text-muted-foreground/70">
                Open settings
                <ArrowRight className="h-3 w-3 transition-transform group-hover:translate-x-0.5" />
              </div>
            ) : null}
          </div>
        </button>
        <Badge className={`border text-[11px] font-normal ${rollupStyle.badge}`}>
          <span className="mr-1">{rollupStyle.icon}</span>
          {rollupStyle.label}
        </Badge>
      </header>

      <div className="space-y-3">
        {integration.checks.map((c) => (
          <CheckCell key={c.key} integrationKey={integration.key} check={c} />
        ))}
      </div>

      {/* v0.0.30 — compact sync-coverage strip anchored inside the Adsolut card
          when the integration is configured. Hidden when the connection check
          is not OK (no point showing gap counts when the connection itself
          is broken — the connection-cell already calls out the bigger
          problem). */}
      {integration.key === "adsolut" && integration.status !== "Critical" ? (
        <div className="mt-3 border-t border-glass pt-3">
          <AdsolutCoverageStrip />
        </div>
      ) : null}

      {integration.actions.length > 0 ? (
        <footer className="mt-auto flex justify-end gap-2 pt-3">
          {integration.actions.map((a) => (
            <TileActionButton key={a.key} action={a} />
          ))}
        </footer>
      ) : null}
    </article>
  );
}

/// Compact coverage row anchored under the Adsolut integration on the
/// dashboard. Renders a green "Fully covered" pill when every bucket is
/// zero, or a strip of amber chips with the per-bucket counts otherwise.
/// Click-through navigates to the deep-linked coverage page. Drift counts
/// are intentionally NOT toggle-suppressed here — the dashboard view is
/// always-honest about the universe; the settings tile handles the
/// "push-tak resolves it within one tick" courtesy hide.
function AdsolutCoverageStrip() {
  const counts = useQuery({
    queryKey: ["integrations", "adsolut", "coverage"] as const,
    queryFn: () => adsolutApi.coverageCounts(),
    // Same 30s cadence as the parent tile so a missed SignalR push still
    // refreshes within one minute.
    refetchInterval: 30_000,
  });

  if (counts.isLoading || !counts.data) return null;

  const c = counts.data;
  const total =
    c.companiesSdOnly +
    c.companiesDrift +
    c.contactLinksUnsynced +
    c.contactLinksDrift +
    c.contactsPureSd;

  type Cell = {
    label: string;
    value: number;
    bucket: { tab: "companies" | "contacts"; bucket: string };
  };
  const cells: Cell[] = [
    {
      label: "SD-only",
      value: c.companiesSdOnly,
      bucket: { tab: "companies", bucket: "sd-only" },
    },
    {
      label: "drift",
      value: c.companiesDrift,
      bucket: { tab: "companies", bucket: "drift" },
    },
    {
      label: "links unsynced",
      value: c.contactLinksUnsynced,
      bucket: { tab: "contacts", bucket: "links-unsynced" },
    },
    {
      label: "links drift",
      value: c.contactLinksDrift,
      bucket: { tab: "contacts", bucket: "links-drift" },
    },
    {
      label: "pure SD",
      value: c.contactsPureSd,
      bucket: { tab: "contacts", bucket: "pure-sd" },
    },
  ];

  return (
    <div className="flex flex-wrap items-center gap-2 text-[11px]">
      <span className="text-muted-foreground/70">Sync coverage</span>
      {total === 0 ? (
        <Link
          to="/settings/integrations/adsolut/coverage"
          search={{ tab: "companies", bucket: undefined, search: undefined, page: undefined }}
          className="inline-flex items-center gap-1 rounded-full border border-emerald-400/30 bg-emerald-500/10 px-2 py-0.5 text-emerald-300 hover:bg-emerald-500/15"
        >
          <CheckCircle2 className="h-3 w-3" />
          Fully covered
        </Link>
      ) : (
        <>
          {cells
            .filter((cell) => cell.value > 0)
            .map((cell) => (
              <Link
                key={cell.label}
                to="/settings/integrations/adsolut/coverage"
                search={{
                  tab: cell.bucket.tab,
                  bucket: cell.bucket.bucket,
                  search: undefined,
                  page: undefined,
                }}
                className="inline-flex items-center gap-1 rounded-full border border-amber-400/30 bg-amber-500/[0.08] px-2 py-0.5 text-amber-200 hover:bg-amber-500/[0.15]"
              >
                <span className="tabular-nums font-medium">{cell.value}</span>
                <span>{cell.label}</span>
              </Link>
            ))}
          <Link
            to="/settings/integrations/adsolut/coverage"
            search={{
              tab: "companies",
              bucket: undefined,
              search: undefined,
              page: undefined,
            }}
            className="inline-flex items-center gap-1 text-muted-foreground hover:text-foreground"
          >
            View
            <ArrowRight className="h-3 w-3" />
          </Link>
        </>
      )}
    </div>
  );
}

function TileActionButton({ action }: { action: HealthAction }) {
  const qc = useQueryClient();

  const mut = useMutation({
    mutationFn: () => healthApi.runAction(action.endpoint),
    onSuccess: () => {
      toast.success(`${action.label} queued`);
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      qc.invalidateQueries({ queryKey: ["system", "health"] });
    },
    onError: (err) => {
      toast.error(
        err instanceof ApiError
          ? `${action.label} failed (${err.status})`
          : `${action.label} failed`,
      );
    },
  });

  const onClick = () => {
    if (action.confirmMessage && !window.confirm(action.confirmMessage)) return;
    mut.mutate();
  };

  return (
    <Button
      size="sm"
      className="h-8 gap-1.5"
      onClick={onClick}
      disabled={mut.isPending}
    >
      <RefreshCw className={cn("h-3.5 w-3.5", mut.isPending && "animate-spin")} />
      {mut.isPending ? "Queueing…" : action.label}
    </Button>
  );
}

function CheckCell({
  integrationKey,
  check,
}: {
  integrationKey: string;
  check: SubsystemHealth;
}) {
  const style = STATUS_STYLES[check.status];
  // Backend stamps "Next sync" as a HealthDetail on the Sync check; we
  // surface it as a small relative-time line so the admin sees the next
  // tick at a glance without leaving the dashboard.
  const nextSyncIso = check.details.find((d) => d.label === "Next sync")?.value ?? null;

  return (
    <div className="flex items-start gap-2">
      <span className={cn("mt-1.5 inline-block h-2 w-2 shrink-0 rounded-full", style.dot)} />
      <div className="min-w-0 flex-1 space-y-0.5">
        <div className="flex items-center gap-2">
          <span className="text-sm font-medium text-foreground">{check.label}</span>
          {check.status !== "Ok" ? (
            <span className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
              {style.label}
            </span>
          ) : null}
        </div>
        <p className="text-xs text-muted-foreground">{check.summary}</p>
        {nextSyncIso ? <NextSyncHint iso={nextSyncIso} /> : null}
        {check.actions.length > 0 ? (
          <div className="flex flex-wrap gap-1.5 pt-1">
            {check.actions.map((a) => (
              <ActionButton key={a.key} integrationKey={integrationKey} action={a} />
            ))}
          </div>
        ) : null}
      </div>
    </div>
  );
}

/// Renders "Next sync ~ in 4 min" / "Next sync ~ now". Re-renders every
/// 30 seconds so the relative phrase doesn't go stale while the tab stays
/// open. The absolute timestamp lives in the title for hover-disclosure.
function NextSyncHint({ iso }: { iso: string }) {
  const [, force] = React.useReducer((n: number) => n + 1, 0);
  React.useEffect(() => {
    const id = window.setInterval(force, 30_000);
    return () => window.clearInterval(id);
  }, []);

  const target = new Date(iso).getTime();
  if (Number.isNaN(target)) return null;
  const phrase = formatRelative(target - Date.now());
  return (
    <p className="text-[11px] text-muted-foreground/70" title={new Date(iso).toLocaleString()}>
      Next sync {phrase}
    </p>
  );
}

function formatRelative(deltaMs: number): string {
  const absSec = Math.abs(deltaMs) / 1000;
  if (absSec < 60) return deltaMs <= 0 ? "due now" : "in <1 min";
  const mins = Math.round(absSec / 60);
  if (mins < 60) return deltaMs > 0 ? `in ${mins} min` : `${mins} min ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return deltaMs > 0 ? `in ${hours}h` : `${hours}h ago`;
  const days = Math.round(hours / 24);
  return deltaMs > 0 ? `in ${days}d` : `${days}d ago`;
}

function ActionButton({
  integrationKey: _integrationKey,
  action,
}: {
  integrationKey: string;
  action: HealthAction;
}) {
  const qc = useQueryClient();

  const mut = useMutation({
    mutationFn: () => healthApi.runAction(action.endpoint),
    onSuccess: () => {
      toast.success(`${action.label} done`);
      // The SignalR push should arrive shortly, but invalidate eagerly so a
      // browser without WebSocket support still sees the row flip.
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      qc.invalidateQueries({ queryKey: ["system", "health"] });
    },
    onError: (err) => {
      toast.error(
        err instanceof ApiError ? `${action.label} failed (${err.status})` : `${action.label} failed`,
      );
    },
  });

  const onClick = () => {
    if (action.confirmMessage && !window.confirm(action.confirmMessage)) return;
    mut.mutate();
  };

  return (
    <Button size="sm" variant="ghost" className="h-7 px-2 text-xs" onClick={onClick} disabled={mut.isPending}>
      {mut.isPending ? "…" : action.label}
    </Button>
  );
}

// ---- Adsolut ERP read-mirror cards (v0.0.60) ------------------------

/// Returns the most recent successful-sync ISO timestamp (delta or full), and
/// whether the last recorded error is still "current" — i.e. no successful
/// sync has run since it. Drives the card's status badge so a transient error
/// that a later tick recovered from doesn't keep the card amber forever.
function deriveMirrorState(s: {
  lastDeltaSyncUtc: string | null;
  lastFullSyncUtc: string | null;
  lastError: string | null;
  lastErrorUtc: string | null;
}): { lastSyncUtc: string | null; errorIsCurrent: boolean } {
  const times = [s.lastDeltaSyncUtc, s.lastFullSyncUtc]
    .filter((t): t is string => !!t)
    .map((t) => new Date(t).getTime())
    .filter((n) => !Number.isNaN(n));
  const lastSuccess = times.length ? Math.max(...times) : null;
  const lastSyncUtc =
    lastSuccess === null ? null : new Date(lastSuccess).toISOString();

  const errAt = s.lastErrorUtc ? new Date(s.lastErrorUtc).getTime() : NaN;
  const errorIsCurrent =
    !!s.lastError &&
    !Number.isNaN(errAt) &&
    (lastSuccess === null || errAt > lastSuccess);

  return { lastSyncUtc, errorIsCurrent };
}

/// Presentational card for an Adsolut ERP mirror — mirrors IntegrationCard's
/// shell so the three Adsolut cards (companies/contacts, ERP, Orders) read as
/// one family. Shows last-sync + next-sync, a counters strip, the current
/// sync error (if any), and a "Sync now" button.
function MirrorCard({
  title,
  subtitle,
  icon,
  lastSyncUtc,
  nextSyncUtc,
  error,
  errorIsCurrent,
  counters,
  onSync,
  syncing,
}: {
  title: string;
  subtitle: string;
  icon: React.ReactNode;
  lastSyncUtc: string | null;
  nextSyncUtc: string | null;
  error: string | null;
  errorIsCurrent: boolean;
  counters: { label: string; value: number }[];
  onSync: () => void;
  syncing: boolean;
}) {
  const status: HealthStatus = errorIsCurrent ? "Warning" : "Ok";
  const style = STATUS_STYLES[status];

  return (
    <article className="group flex flex-col rounded-lg border border-glass-strong bg-glass p-4">
      <header className="mb-3 flex items-center justify-between gap-3">
        <Link
          to="/settings/integrations/adsolut"
          className="-m-1 flex items-center gap-2.5 rounded-md p-1 text-left transition-colors hover:bg-glass-hover"
        >
          <div className="flex h-8 w-8 items-center justify-center rounded-sm border border-glass bg-glass text-primary">
            {icon}
          </div>
          <div className="min-w-0">
            <div className="text-sm font-semibold text-foreground">{title}</div>
            <div className="text-[11px] text-muted-foreground/70">{subtitle}</div>
          </div>
        </Link>
        <Badge className={`border text-[11px] font-normal ${style.badge}`}>
          <span className="mr-1">{style.icon}</span>
          {style.label}
        </Badge>
      </header>

      <div className="space-y-1.5">
        <div className="flex items-center justify-between gap-2 text-xs">
          <span className="text-muted-foreground">Last sync</span>
          <span
            className="font-medium text-foreground"
            title={lastSyncUtc ? new Date(lastSyncUtc).toLocaleString() : undefined}
          >
            {lastSyncUtc ? formatRelative(new Date(lastSyncUtc).getTime() - Date.now()) : "never"}
          </span>
        </div>
        {nextSyncUtc ? <NextSyncHint iso={nextSyncUtc} /> : null}

        <div className="flex flex-wrap gap-x-3 gap-y-0.5 pt-0.5 text-[11px] text-muted-foreground">
          {counters.map((c) => (
            <span key={c.label}>
              <span className="tabular-nums font-medium text-foreground">{c.value}</span> {c.label}
            </span>
          ))}
        </div>

        {error && errorIsCurrent ? (
          <div className="mt-1 inline-flex items-start gap-1 text-[11px] text-rose-500 dark:text-rose-300">
            <AlertTriangle className="mt-px h-3 w-3 shrink-0" />
            <span className="line-clamp-2">{error}</span>
          </div>
        ) : null}
      </div>

      <footer className="mt-auto flex justify-end pt-3">
        <Button size="sm" className="h-8 gap-1.5" onClick={onSync} disabled={syncing}>
          <RefreshCw className={cn("h-3.5 w-3.5", syncing && "animate-spin")} />
          {syncing ? "Queueing…" : "Sync now"}
        </Button>
      </footer>
    </article>
  );
}

function AdsolutErpSyncCard() {
  const qc = useQueryClient();
  const key = ["admin", "adsolut", "erp-sales-receipts", "state"] as const;
  const query = useQuery({
    queryKey: key,
    queryFn: () => adsolutApi.erpSalesReceiptsState(),
    refetchInterval: 30_000,
  });

  const sync = useMutation({
    mutationFn: () => adsolutApi.triggerErpSalesReceiptsSync(),
    onSuccess: () => {
      toast.success("ERP sync queued");
      qc.invalidateQueries({ queryKey: key });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `ERP sync failed (${err.status})` : "ERP sync failed"),
  });

  const s: AdsolutErpSalesReceiptsState | undefined = query.data;
  if (!s || !s.enabled) return null;

  const { lastSyncUtc, errorIsCurrent } = deriveMirrorState(s);

  return (
    <MirrorCard
      title="Adsolut · ERP"
      subtitle="Sales receipts"
      icon={<Receipt className="h-4 w-4" />}
      lastSyncUtc={lastSyncUtc}
      nextSyncUtc={s.nextSyncUtc}
      error={s.lastError}
      errorIsCurrent={errorIsCurrent}
      counters={[
        { label: "mirrored", value: s.totalMirrored },
        { label: "last tick", value: s.receiptsUpserted },
      ]}
      onSync={() => sync.mutate()}
      syncing={sync.isPending}
    />
  );
}

function AdsolutOrdersSyncCard() {
  const qc = useQueryClient();
  const key = ["admin", "adsolut", "erp-orders", "state"] as const;
  const query = useQuery({
    queryKey: key,
    queryFn: () => adsolutApi.erpOrdersState(),
    refetchInterval: 30_000,
  });

  const sync = useMutation({
    mutationFn: () => adsolutApi.triggerErpOrdersSync(),
    onSuccess: () => {
      toast.success("Order sync queued");
      qc.invalidateQueries({ queryKey: key });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Order sync failed (${err.status})` : "Order sync failed"),
  });

  const s: AdsolutErpOrdersState | undefined = query.data;
  if (!s || !s.enabled) return null;

  const { lastSyncUtc, errorIsCurrent } = deriveMirrorState(s);

  return (
    <MirrorCard
      title="Adsolut · Orders"
      subtitle="Orders + supplier orders"
      icon={<Package className="h-4 w-4" />}
      lastSyncUtc={lastSyncUtc}
      nextSyncUtc={s.nextSyncUtc}
      error={s.lastError}
      errorIsCurrent={errorIsCurrent}
      counters={[
        { label: "mirrored", value: s.totalMirrored },
        { label: "orders", value: s.ordersUpserted },
        { label: "supplier", value: s.supplierOrdersUpserted },
      ]}
      onSync={() => sync.mutate()}
      syncing={sync.isPending}
    />
  );
}
