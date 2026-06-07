import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import {
  BarChart3,
  Users,
  User as UserIcon,
  UserCircle2,
  ChevronLeft,
  ChevronRight,
} from "lucide-react";
import { statisticsApi, type StatisticTileDto } from "@/lib/ticket-api";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

function formatHours(value: number): string {
  // Whole numbers stay clean; fractional hours show one decimal.
  return Number.isInteger(value) ? `${value}h` : `${value.toFixed(1)}h`;
}

// Unit-aware value formatter: hours render as "Xh", counts as plain integers.
function formatValue(value: number, unit: string): string {
  return unit === "hours" ? formatHours(value) : `${Math.round(value)}`;
}

function ScopeBadge({ tile }: { tile: StatisticTileDto }) {
  if (tile.scope === "team") {
    return (
      <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
        <Users className="h-3 w-3" /> Team
      </span>
    );
  }
  if (tile.scope === "users") {
    return (
      <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
        <Users className="h-3 w-3" /> Compare
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
  // Per-tile period navigation. 0 = current period; negative = earlier.
  // Ephemeral (not persisted) — resets when the tile remounts.
  const [offset, setOffset] = React.useState(0);
  const q = useQuery({
    queryKey: ["statistics", "tile-data", tile.id, offset],
    queryFn: () => statisticsApi.tileData(tile.id, offset),
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
          <div className="mt-0.5 flex items-center gap-1">
            <button
              type="button"
              onClick={() => setOffset((o) => o - 1)}
              title="Previous period"
              className="rounded p-0.5 text-muted-foreground hover:bg-glass-hover hover:text-foreground"
            >
              <ChevronLeft className="h-3.5 w-3.5" />
            </button>
            <span className="min-w-0 flex-1 truncate text-center text-xs text-muted-foreground">
              {q.data?.periodLabel ?? " "}
            </span>
            <button
              type="button"
              onClick={() => setOffset((o) => Math.min(o + 1, 0))}
              disabled={offset >= 0}
              title="Next period"
              className="rounded p-0.5 text-muted-foreground hover:bg-glass-hover hover:text-foreground disabled:opacity-30 disabled:hover:bg-transparent"
            >
              <ChevronRight className="h-3.5 w-3.5" />
            </button>
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
        <KpiBody total={q.data?.total ?? 0} unit={q.data?.unit ?? "hours"} />
      ) : (
        <BarBody
          points={q.data?.points ?? []}
          seriesLabels={q.data?.seriesLabels ?? null}
          unit={q.data?.unit ?? "hours"}
        />
      )}
    </section>
  );
}

function KpiBody({ total, unit }: { total: number; unit: string }) {
  return (
    <div className="flex flex-1 flex-col items-center justify-center py-4">
      <div className="text-display-md font-semibold tabular-nums text-foreground">
        {formatValue(total, unit)}
      </div>
      <div className="mt-1 text-xs uppercase tracking-wider text-muted-foreground/70">
        {unit === "hours" ? "worked" : unit}
      </div>
    </div>
  );
}

function BarBody({
  points,
  seriesLabels,
  unit,
}: {
  points: { label: string; value: number; value2?: number | null; segments?: number[] | null }[];
  seriesLabels?: string[] | null;
  unit: string;
}) {
  if (points.length === 0) {
    return (
      <div className="flex flex-1 items-center justify-center text-xs text-muted-foreground">
        No data for this period yet.
      </div>
    );
  }

  // Three render modes:
  //  - segments: N-series stacked (e.g. hours per status group, per technician)
  //  - twoSeries: billable vs non-billable (value + value2)
  //  - single: a plain bar
  const segmentMode = points.some((p) => p.segments && p.segments.length > 0);
  const twoSeries = !segmentMode && !!seriesLabels && seriesLabels.length === 2;

  const rowTotal = (p: { value: number; value2?: number | null; segments?: number[] | null }) =>
    segmentMode
      ? (p.segments ?? []).reduce((a, b) => a + b, 0)
      : twoSeries
        ? p.value + (p.value2 ?? 0)
        : p.value;
  const max = Math.max(...points.map(rowTotal), 0.0001);

  return (
    <div className="flex flex-1 flex-col gap-1.5 overflow-y-auto pr-1">
      {(segmentMode || twoSeries) && seriesLabels && (
        <div className="mb-0.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-[10px] text-muted-foreground">
          {seriesLabels.map((label, si) => (
            <span key={label} className="inline-flex items-center gap-1">
              <span className={cn("h-2 w-2 rounded-sm", seriesColor(si, twoSeries))} /> {label}
            </span>
          ))}
        </div>
      )}
      {points.map((p, i) => {
        const total = rowTotal(p);
        return (
          <div key={`${p.label}-${i}`} className="flex items-center gap-2 text-xs">
            <span className="w-16 shrink-0 truncate text-muted-foreground" title={p.label}>
              {p.label}
            </span>
            {/* Exact proportional widths (no percentage floor, which would make
                small values collapse to the same minimum and look equal). A 2px
                floor only keeps a non-zero value from disappearing entirely. */}
            <div className="relative flex h-4 flex-1 overflow-hidden rounded bg-glass">
              {segmentMode ? (
                (p.segments ?? []).map((seg, si) => (
                  <div
                    key={si}
                    className={cn("h-full shrink-0", seriesColor(si, false))}
                    style={{ width: `${(seg / max) * 100}%`, minWidth: seg > 0 ? 2 : 0 }}
                  />
                ))
              ) : (
                <>
                  <div
                    className="h-full shrink-0 bg-gradient-to-r from-primary/70 to-primary"
                    style={{ width: `${(p.value / max) * 100}%`, minWidth: p.value > 0 ? 2 : 0 }}
                  />
                  {twoSeries && (
                    <div
                      className="h-full shrink-0 bg-muted-foreground/30"
                      style={{
                        width: `${((p.value2 ?? 0) / max) * 100}%`,
                        minWidth: (p.value2 ?? 0) > 0 ? 2 : 0,
                      }}
                    />
                  )}
                </>
              )}
            </div>
            <span
              className={cn(
                "shrink-0 text-right font-mono tabular-nums text-foreground",
                segmentMode || twoSeries ? "w-[4.5rem]" : "w-12",
              )}
            >
              {segmentMode
                ? formatHours(total)
                : twoSeries
                  ? `${formatHours(p.value)}/${formatHours(total)}`
                  : formatValue(p.value, unit)}
            </span>
          </div>
        );
      })}
    </div>
  );
}

// Colour per stacked series. The billable two-series keeps its semantic
// primary/grey; the N-series stacked uses a small glass-friendly palette.
const SERIES_PALETTE = [
  "bg-primary",
  "bg-sky-400/70",
  "bg-amber-400/70",
  "bg-emerald-400/70",
  "bg-rose-400/70",
];
function seriesColor(index: number, twoSeries: boolean): string {
  if (twoSeries) return index === 0 ? "bg-primary" : "bg-muted-foreground/40";
  return SERIES_PALETTE[index % SERIES_PALETTE.length];
}
