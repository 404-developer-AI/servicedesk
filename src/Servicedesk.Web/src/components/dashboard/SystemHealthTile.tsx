import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import {
  Activity,
  AlertTriangle,
  CheckCircle2,
  ChevronRight,
} from "lucide-react";
import { healthApi, type HealthStatus, type SubsystemHealth } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";

const HEALTH_QUERY_KEY = ["admin", "health"] as const;

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

/// Admin-only dashboard tile. Shows the roll-up status + one row per
/// subsystem (coloured dot + label + one-line summary). Click-through
/// navigates to the full Health settings page. Uses the same query key
/// as that page so toggling acknowledge there refreshes the tile here.
export function SystemHealthTile() {
  const navigate = useNavigate();
  const query = useQuery({
    queryKey: HEALTH_QUERY_KEY,
    queryFn: () => healthApi.get(),
    refetchInterval: 30_000,
  });

  const rollup = query.data?.status ?? "Ok";
  const style = STATUS_STYLES[rollup];

  return (
    <section className="glass-card p-5">
      <header className="mb-3 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Activity className="h-4 w-4 text-primary" />
          System health
        </div>
        {query.data ? (
          <Badge className={`border text-[11px] font-normal ${style.badge}`}>
            <span className="mr-1">{style.icon}</span>
            {style.label}
          </Badge>
        ) : null}
      </header>

      {query.isLoading ? (
        <Skeleton className="h-28 w-full" />
      ) : query.isError ? (
        <p className="text-xs text-muted-foreground">
          Unable to load health — check your session.
        </p>
      ) : query.data ? (
        <>
          <ul className="divide-y divide-white/[0.04]">
            {query.data.subsystems.map((s) => (
              <SubsystemRow key={s.key} subsystem={s} />
            ))}
          </ul>
          <button
            type="button"
            onClick={() => void navigate({ to: "/settings/health" })}
            className="mt-3 flex w-full items-center justify-between rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs text-muted-foreground transition hover:bg-white/[0.04] hover:text-foreground"
          >
            <span>Open Health page</span>
            <ChevronRight className="h-3.5 w-3.5" />
          </button>
        </>
      ) : null}
    </section>
  );
}

function SubsystemRow({ subsystem }: { subsystem: SubsystemHealth }) {
  const style = STATUS_STYLES[subsystem.status];
  return (
    <li className="flex items-start gap-3 py-2">
      <span className={`mt-1 inline-block h-2 w-2 shrink-0 rounded-full ${style.dot}`} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="text-sm font-medium text-foreground">{subsystem.label}</span>
          {subsystem.status !== "Ok" ? (
            <span className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
              {style.label}
            </span>
          ) : null}
        </div>
        <p className="truncate text-xs text-muted-foreground">{subsystem.summary}</p>
      </div>
    </li>
  );
}
