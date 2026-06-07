import { useQuery } from "@tanstack/react-query";
import { BarChart3, Users, User as UserIcon, UserCircle2 } from "lucide-react";
import { statisticsApi, type StatisticTileDto } from "@/lib/ticket-api";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

function formatHours(value: number): string {
  // Whole numbers stay clean; fractional hours show one decimal.
  return Number.isInteger(value) ? `${value}h` : `${value.toFixed(1)}h`;
}

function ScopeBadge({ tile }: { tile: StatisticTileDto }) {
  if (tile.scope === "team") {
    return (
      <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
        <Users className="h-3 w-3" /> Team
      </span>
    );
  }
  if (tile.scope === "user") {
    return (
      <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
        <UserIcon className="h-3 w-3" /> {tile.scopeUserEmail ?? "Technician"}
      </span>
    );
  }
  return (
    <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
      <UserCircle2 className="h-3 w-3" /> You
    </span>
  );
}

/// Renders a single statistic tile by fetching its computed data. Chart type
/// drives the body: 'kpi' = a big number, 'bar' = custom glass horizontal
/// bars (richer chart types arrive with recharts in a later increment).
export function StatisticTileCard({ tile }: { tile: StatisticTileDto }) {
  const q = useQuery({
    queryKey: ["statistics", "tile-data", tile.id],
    queryFn: () => statisticsApi.tileData(tile.id),
    staleTime: 60_000,
  });

  return (
    <section className="glass-card flex h-full flex-col p-5">
      <header className="mb-3 flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2 text-sm font-medium text-foreground">
            <BarChart3 className="h-4 w-4 shrink-0 text-primary" />
            <span className="truncate">{tile.title}</span>
          </div>
          <div className="mt-0.5 truncate text-xs text-muted-foreground">
            {q.data?.periodLabel ?? " "}
          </div>
        </div>
        <ScopeBadge tile={tile} />
      </header>

      {q.isLoading ? (
        <Skeleton className="h-24 w-full" />
      ) : q.isError ? (
        <div className="flex flex-1 items-center justify-center text-xs text-muted-foreground">
          Could not load this tile.
        </div>
      ) : tile.chartType === "kpi" ? (
        <KpiBody total={q.data?.total ?? 0} />
      ) : (
        <BarBody points={q.data?.points ?? []} />
      )}
    </section>
  );
}

function KpiBody({ total }: { total: number }) {
  return (
    <div className="flex flex-1 flex-col items-center justify-center py-4">
      <div className="text-display-md font-semibold tabular-nums text-foreground">
        {formatHours(total)}
      </div>
      <div className="mt-1 text-xs uppercase tracking-wider text-muted-foreground/70">
        worked
      </div>
    </div>
  );
}

function BarBody({ points }: { points: { label: string; value: number }[] }) {
  if (points.length === 0) {
    return (
      <div className="flex flex-1 items-center justify-center text-xs text-muted-foreground">
        No data for this period yet.
      </div>
    );
  }
  const max = Math.max(...points.map((p) => p.value), 0.0001);
  return (
    <div className="flex flex-1 flex-col gap-1.5 overflow-y-auto pr-1">
      {points.map((p, i) => (
        <div key={`${p.label}-${i}`} className="flex items-center gap-2 text-xs">
          <span className="w-16 shrink-0 truncate text-muted-foreground" title={p.label}>
            {p.label}
          </span>
          <div className="relative h-4 flex-1 overflow-hidden rounded bg-glass">
            <div
              className={cn(
                "absolute inset-y-0 left-0 rounded bg-gradient-to-r from-primary/70 to-primary",
              )}
              style={{ width: `${Math.max((p.value / max) * 100, p.value > 0 ? 4 : 0)}%` }}
            />
          </div>
          <span className="w-12 shrink-0 text-right font-mono tabular-nums text-foreground">
            {formatHours(p.value)}
          </span>
        </div>
      ))}
    </div>
  );
}
