import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { toast } from "sonner";
import { CheckCircle2, AlertTriangle, Plug, RefreshCw } from "lucide-react";
import {
  ApiError,
  healthApi,
  integrationsHealthApi,
  type HealthAction,
  type HealthStatus,
  type IntegrationHealth,
  type SubsystemHealth,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import adsolutLogo from "@/assets/integrations/adsolut.ico";

const QUERY_KEY = ["admin", "integrations-health"] as const;

const STATUS_STYLES: Record<
  HealthStatus,
  { badge: string; dot: string; label: string; icon: React.ReactNode }
> = {
  Ok: {
    badge: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    dot: "bg-emerald-400",
    label: "OK",
    icon: <CheckCircle2 className="h-3.5 w-3.5" />,
  },
  Warning: {
    badge: "border-amber-400/30 bg-amber-500/10 text-amber-300",
    dot: "bg-amber-400",
    label: "Warning",
    icon: <AlertTriangle className="h-3.5 w-3.5" />,
  },
  Critical: {
    badge: "border-rose-400/40 bg-rose-500/10 text-rose-300",
    dot: "bg-rose-400",
    label: "Critical",
    icon: <AlertTriangle className="h-3.5 w-3.5" />,
  },
};

// Backend ships a stable `logoKey` per integration; the SPA holds the actual
// asset URLs. New connectors register here when their tile lands.
const LOGOS: Record<string, string> = {
  adsolut: adsolutLogo,
};

const DETAIL_ROUTES: Record<string, string> = {
  adsolut: "/settings/integrations/adsolut",
};

/// Admin-only dashboard tile. Shows a compact roll-up of every configured
/// integration with its Connection + Sync checks side by side. Renders
/// nothing when the backend reports an empty integrations list, so a vanilla
/// install does not show an empty card.
export function IntegrationsHealthTile() {
  const query = useQuery({
    queryKey: QUERY_KEY,
    queryFn: () => integrationsHealthApi.get(),
    refetchInterval: 30_000,
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
    <section className="glass-card p-4">
      <header className="mb-3 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Plug className="h-4 w-4 text-primary" />
          Integrations
        </div>
        <Badge className={`border text-[11px] font-normal ${style.badge}`}>
          <span className="mr-1">{style.icon}</span>
          {style.label}
        </Badge>
      </header>

      <ul className="divide-y divide-white/[0.04]">
        {integrations.map((i) => (
          <IntegrationRow key={i.key} integration={i} />
        ))}
      </ul>
    </section>
  );
}

function IntegrationRow({ integration }: { integration: IntegrationHealth }) {
  const navigate = useNavigate();
  const logoSrc = LOGOS[integration.logoKey];
  const detailRoute = DETAIL_ROUTES[integration.key];

  return (
    <li className="grid grid-cols-1 items-center gap-3 py-3 sm:grid-cols-[10rem_1fr_1fr_auto]">
      <button
        type="button"
        onClick={() => detailRoute && void navigate({ to: detailRoute })}
        disabled={!detailRoute}
        className={cn(
          "flex items-center gap-2.5 rounded-md px-1 py-0.5 text-left transition-colors",
          detailRoute && "hover:bg-white/[0.03]",
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
          <div className="h-8 w-8 rounded-sm border border-white/10 bg-white/[0.03]" />
        )}
        <span className="text-sm font-medium text-foreground">{integration.name}</span>
      </button>

      {integration.checks.map((c) => (
        <CheckCell key={c.key} integrationKey={integration.key} check={c} />
      ))}

      {/* Tile-level actions, vertically centred at the end of the row. The
          fixed `auto` track keeps the column tight so the check cells
          claim the remaining width. Empty when the integration has no
          tile actions (e.g. not yet authorised). */}
      <div className="flex items-center justify-end self-center">
        {integration.actions.map((a) => (
          <TileActionButton key={a.key} action={a} />
        ))}
      </div>
    </li>
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
