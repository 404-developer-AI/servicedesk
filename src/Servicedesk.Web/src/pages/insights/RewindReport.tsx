import * as React from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import {
  Area,
  AreaChart,
  CartesianGrid,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, FlaskConical, History, PhoneCall } from "lucide-react";
import { useTheme } from "@/app/ThemeProvider";
import { cn } from "@/lib/utils";
import { colorPillStyle } from "@/lib/colorPill";
import { formatDateTimeMedium, formatDayMonthTime, formatTime } from "@/lib/dateFormat";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  insightsApi,
  insightsErrorMessage,
  type RewindGroup,
  type RewindItem,
  type RewindRange,
  type RewindSeries,
  type RewindSnapshot,
} from "@/lib/insights-api";
import { nf, slotColor } from "./insightsFormat";
import { IconButton, Segmented } from "./InsightsControls";
import { countByGroup, diffSnapshots, type DiffRow, type SnapshotDiff } from "./rewindDiff";

const RANGES: ReadonlyArray<{ value: RewindRange; label: string; ms: number }> = [
  { value: "4h", label: "4 hours", ms: 4 * 3_600_000 },
  { value: "24h", label: "24 hours", ms: 24 * 3_600_000 },
  { value: "7d", label: "7 days", ms: 7 * 86_400_000 },
];

/** Compare offsets in minutes; only those ≥ the capture interval are offered. */
const COMPARES: ReadonlyArray<{ value: string; label: string; minutes: number }> = [
  { value: "15", label: "15 min", minutes: 15 },
  { value: "30", label: "30 min", minutes: 30 },
  { value: "60", label: "1 hour", minutes: 60 },
  { value: "1440", label: "1 day", minutes: 1440 },
  { value: "10080", label: "1 week", minutes: 10080 },
];

// Per-viewer conveniences.
const VIEW_KEY = "sd-insights-rewind-view";
const RANGE_KEY = "sd-insights-rewind-range";
const COMPARE_KEY = "sd-insights-rewind-compare";

function readPref(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writePref(key: string, value: string) {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // storage unavailable — the choice still applies for this visit
  }
}

/** Deterministic palette slot (0–7) for a group key. */
function keySlot(key: string): number {
  let h = 0;
  for (let i = 0; i < key.length; i++) h = (h * 31 + key.charCodeAt(i)) | 0;
  return Math.abs(h) % 8;
}

const isoMinus = (iso: string, minutes: number) => new Date(Date.parse(iso) - minutes * 60_000).toISOString();

/// v0.1.31 — Rewind: how the tickets in a tracked view shifted over time.
/// One snapshot per interval (server time); a point on the chart opens the
/// view as it stood then, compared with an earlier moment.
export function RewindReportView() {
  const views = useQuery({
    queryKey: ["insights", "rewind", "views"],
    queryFn: () => insightsApi.rewindViews(),
    staleTime: 5 * 60_000,
  });

  if (views.isLoading) return <div className="glass-card h-[520px] animate-pulse" />;
  if (views.isError || !views.data)
    return (
      <div className="glass-card p-6 text-sm text-muted-foreground">
        {insightsErrorMessage(views.error) ?? "Rewind could not be loaded. Try again in a moment."}
      </div>
    );
  if (views.data.views.length === 0)
    return (
      <div className="glass-card flex flex-col items-start gap-2 p-6">
        <History className="h-5 w-5 text-primary" />
        <p className="text-sm font-medium text-foreground">No views are tracked yet</p>
        <p className="max-w-xl text-sm text-muted-foreground">
          An admin turns on <span className="font-medium text-foreground">Track in Rewind</span> for a view under
          Settings → Views. From then on the view is captured every {views.data.intervalMinutes} minutes, and
          its history shows up here.
        </p>
      </div>
    );

  return <Rewind views={views.data.views} intervalMinutes={views.data.intervalMinutes} />;
}

function Rewind({ views, intervalMinutes }: { views: Array<{ id: string; name: string }>; intervalMinutes: number }) {
  const { mode } = useTheme();
  const dark = mode === "dark";

  const [viewId, setViewId] = React.useState(() => {
    const stored = readPref(VIEW_KEY);
    return views.some((v) => v.id === stored) ? stored! : views[0].id;
  });
  const [range, setRange] = React.useState<RewindRange>(() => {
    const stored = readPref(RANGE_KEY);
    return stored === "4h" || stored === "7d" ? stored : "24h";
  });
  const compares = COMPARES.filter((c) => c.minutes >= intervalMinutes);
  const [compare, setCompare] = React.useState<string>(() => {
    const stored = readPref(COMPARE_KEY);
    return stored === "off" || compares.some((c) => c.value === stored) ? stored! : compares[0]?.value ?? "off";
  });
  // null = the window ends at the latest slot and follows it.
  const [windowEnd, setWindowEnd] = React.useState<string | null>(null);
  // null = the latest covered slot of the window.
  const [selected, setSelected] = React.useState<string | null>(null);
  const [hidden, setHidden] = React.useState<Set<string>>(new Set());

  function chooseView(id: string) {
    setViewId(id);
    writePref(VIEW_KEY, id);
    setSelected(null);
    setHidden(new Set());
  }
  function chooseRange(r: RewindRange) {
    setRange(r);
    writePref(RANGE_KEY, r);
    setWindowEnd(null);
    setSelected(null);
  }
  function chooseCompare(c: string) {
    setCompare(c);
    writePref(COMPARE_KEY, c);
  }

  const series = useQuery({
    queryKey: ["insights", "rewind", "series", viewId, range, windowEnd],
    queryFn: () => insightsApi.rewindSeries(viewId, range, windowEnd),
    placeholderData: keepPreviousData,
    // Following the latest slot: pick up each new capture.
    refetchInterval: windowEnd === null ? 60_000 : false,
  });
  const data = series.data;

  const lastCovered = React.useMemo(() => {
    if (!data) return null;
    for (let i = data.slots.length - 1; i >= 0; i--) if (data.slots[i].covered) return data.slots[i].t;
    return null;
  }, [data]);
  const at = selected ?? lastCovered;
  const compareMinutes = compare === "off" ? null : Number(compare);
  const compareAt = at && compareMinutes ? isoMinus(at, compareMinutes) : null;

  const current = useQuery({
    queryKey: ["insights", "rewind", "snapshot", viewId, at],
    queryFn: () => insightsApi.rewindSnapshot(viewId, at!),
    enabled: !!at,
    placeholderData: keepPreviousData,
    staleTime: 5 * 60_000,
  });
  const previous = useQuery({
    queryKey: ["insights", "rewind", "snapshot", viewId, compareAt],
    queryFn: () => insightsApi.rewindSnapshot(viewId, compareAt!),
    enabled: !!compareAt,
    placeholderData: keepPreviousData,
    staleTime: 5 * 60_000,
  });

  const span = RANGES.find((r) => r.value === range)!.ms;
  const atLatest = !data || data.toUtc === data.latestSlotUtc;
  const short = range !== "7d";
  const fmtSlot = (iso: string) => (short ? formatTime(iso) : formatDayMonthTime(iso));

  // Stable per group key, so a group keeps its colour when other groups
  // appear or disappear between moments. Status/priority/float groups bring
  // their own colour; the catch-all list is neutral.
  const colorFor = React.useCallback(
    (g: RewindGroup) =>
      g.color ?? (g.key === "__all__" ? (dark ? "#8a8f98" : "#898781") : slotColor(keySlot(g.key), dark)),
    [dark],
  );

  const diff = React.useMemo(
    () => (current.data?.covered ? diffSnapshots(current.data, compareAt && previous.data?.covered ? previous.data : null) : null),
    [current.data, previous.data, compareAt],
  );

  return (
    <section className="flex flex-col gap-4" aria-labelledby="insights-rewind">
      {/* ---- filter row ---------------------------------------------------- */}
      <div className="glass-card sd-insights-filters flex flex-wrap items-center gap-3 p-3">
        <Select value={viewId} onValueChange={chooseView}>
          <SelectTrigger className="h-8 w-56 text-sm" aria-label="View">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {views.map((v) => (
              <SelectItem key={v.id} value={v.id}>
                {v.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>

        <Segmented
          ariaLabel="Window"
          value={range}
          options={RANGES}
          onChange={(v) => chooseRange(v as RewindRange)}
        />

        <div className="flex items-center gap-1">
          <IconButton
            label="Earlier"
            onClick={() => {
              if (!data) return;
              setWindowEnd(new Date(Date.parse(data.toUtc) - span).toISOString());
              setSelected(null);
            }}
            disabled={!data}
          >
            <ChevronLeft className="h-4 w-4" />
          </IconButton>
          <span className="min-w-48 px-1 text-center text-sm font-medium tabular-nums text-foreground">
            {data ? `${formatDayMonthTime(data.fromUtc)} – ${atLatest ? "now" : formatDayMonthTime(data.toUtc)}` : "…"}
          </span>
          <IconButton
            label="Later"
            onClick={() => {
              if (!data) return;
              const next = Date.parse(data.toUtc) + span;
              setWindowEnd(next >= Date.parse(data.latestSlotUtc) ? null : new Date(next).toISOString());
              setSelected(null);
            }}
            disabled={atLatest}
          >
            <ChevronRight className="h-4 w-4" />
          </IconButton>
        </div>

        <span className="ml-auto text-xs text-muted-foreground">
          Snapshot every {intervalMinutes} min · server time
        </span>
      </div>

      {series.isError && !data ? (
        <div className="glass-card p-6 text-sm text-muted-foreground">
          {insightsErrorMessage(series.error) ?? "The history could not be loaded."}
        </div>
      ) : !data ? (
        <div className="glass-card h-[420px] animate-pulse" />
      ) : (
        <div className={cn("flex flex-col gap-4 transition-opacity", series.isFetching && series.isPlaceholderData && "opacity-60")}>
          <RewindChart
            series={data}
            hidden={hidden}
            selected={at}
            compareAt={compareAt}
            colorFor={colorFor}
            fmtSlot={fmtSlot}
            onSelect={setSelected}
            onToggle={(key) =>
              setHidden((prev) => {
                const next = new Set(prev);
                if (next.has(key)) next.delete(key);
                else next.add(key);
                return next;
              })
            }
            currentCounts={current.data?.covered ? countByGroup(current.data) : null}
          />

          {/* ---- compare bar --------------------------------------------- */}
          <div className="glass-card flex flex-wrap items-center gap-3 p-3">
            <div className="flex items-baseline gap-2">
              <span className="text-[11px] font-medium uppercase tracking-[0.08em] text-muted-foreground">Showing</span>
              <span className="text-sm font-semibold tabular-nums text-foreground">
                {at ? formatDateTimeMedium(at) : "—"}
              </span>
            </div>
            <div className="flex items-center gap-2">
              <span className="text-xs text-muted-foreground">compared with</span>
              <Segmented
                ariaLabel="Compare with"
                value={compare}
                options={[...compares, { value: "off", label: "Off" }]}
                onChange={chooseCompare}
              />
              <span className="text-xs text-muted-foreground">{compareMinutes ? "earlier" : ""}</span>
            </div>
            {diff && diff.hasCompare && (
              <div className="ml-auto flex flex-wrap items-center gap-1.5">
                <SummaryChip tone="new" n={diff.newCount} label="new" />
                <SummaryChip tone="gone" n={diff.gone.length} label="left the view" />
                <SummaryChip tone="moved" n={diff.movedCount} label="moved" />
                <SummaryChip tone="changed" n={diff.changedCount} label="changed" />
              </div>
            )}
          </div>

          {/* ---- the view as it stood -------------------------------------- */}
          {!at ? (
            <div className="glass-card p-6 text-sm text-muted-foreground">
              No snapshot in this window yet. Snapshots start once the view is tracked; step back with ‹ or pick
              another window.
            </div>
          ) : current.isError ? (
            <div className="glass-card p-6 text-sm text-muted-foreground">
              {insightsErrorMessage(current.error) ?? "The snapshot could not be loaded."}
            </div>
          ) : !current.data ? (
            <div className="glass-card h-[360px] animate-pulse" />
          ) : !current.data.covered ? (
            <div className="glass-card p-6 text-sm text-muted-foreground">
              Nothing was captured at {formatDateTimeMedium(at)} — the view was not tracked yet, or the server was
              not running. Pick a point on the chart that has data.
            </div>
          ) : (
            <SnapshotList
              snapshot={current.data}
              diff={diff!}
              compareLabel={compareAt && previous.data?.covered ? formatDateTimeMedium(compareAt) : null}
              compareMissing={!!compareAt && previous.data !== undefined && !previous.data.covered}
              colorFor={colorFor}
              hidden={hidden}
              dimmed={current.isPlaceholderData}
            />
          )}
        </div>
      )}
    </section>
  );
}

// ---- chart -------------------------------------------------------------------

type ChartRow = { t: string; covered: boolean } & Record<string, number | string | boolean | null>;

function RewindChart({
  series,
  hidden,
  selected,
  compareAt,
  colorFor,
  fmtSlot,
  onSelect,
  onToggle,
  currentCounts,
}: {
  series: RewindSeries;
  hidden: Set<string>;
  selected: string | null;
  compareAt: string | null;
  colorFor: (g: RewindGroup) => string;
  fmtSlot: (iso: string) => string;
  onSelect: (iso: string) => void;
  onToggle: (key: string) => void;
  currentCounts: Map<string, number> | null;
}) {
  const groups = series.groups;
  const visible = groups.map((g) => ({ g, color: colorFor(g) })).filter(({ g }) => !hidden.has(g.key));

  const rows = React.useMemo<ChartRow[]>(
    () =>
      series.slots.map((s) => {
        const row: ChartRow = { t: s.t, covered: s.covered };
        for (const g of groups) row[g.key] = s.covered ? (s.counts[g.key] ?? 0) : null;
        return row;
      }),
    [series, groups],
  );
  const anyCovered = series.slots.some((s) => s.covered);
  // A step area needs a following covered slot to have width; a snapshot
  // with gaps on both sides (or the very first one) gets a dot instead.
  const isolated = React.useMemo(() => {
    const set = new Set<number>();
    series.slots.forEach((s, i) => {
      if (s.covered && !series.slots[i + 1]?.covered) set.add(i);
    });
    return set;
  }, [series]);
  // The compare moment as a slot on this axis (it may fall outside the window).
  const compareSlot = compareAt
    ? series.slots.find((s) => Date.parse(s.t) <= Date.parse(compareAt) && Date.parse(compareAt) < Date.parse(s.t) + series.intervalMinutes * 60_000)?.t
    : undefined;

  return (
    <div className="glass-card p-5">
      <div className="mb-4">
        <h2 id="insights-rewind" className="text-base font-semibold text-foreground">
          Tickets per group
        </h2>
        <p className="max-w-3xl text-xs text-muted-foreground">
          The view as agents saw it, one point per snapshot: float buckets (Priority, Call-back, Research) and the
          view's own grouping, in its own order. Click a point to open that moment below. Gaps mean nothing was
          captured. Only tickets in your queues are counted.
        </p>
      </div>

      {groups.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {groups.map((g) => {
            const off = hidden.has(g.key);
            const n = currentCounts?.get(g.key);
            return (
              <button
                key={g.key}
                type="button"
                aria-pressed={!off}
                onClick={() => onToggle(g.key)}
                className={cn(
                  "inline-flex h-7 items-center gap-2 rounded-md border border-glass px-2.5 text-xs transition-colors",
                  off ? "text-muted-foreground opacity-60 hover:opacity-100" : "bg-glass text-foreground hover:bg-glass-hover",
                )}
              >
                <span
                  className="h-2.5 w-2.5 rounded-[3px]"
                  style={{ backgroundColor: off ? "transparent" : colorFor(g), boxShadow: off ? `inset 0 0 0 1.5px ${colorFor(g)}` : undefined }}
                />
                {g.label || "All tickets"}
                {n !== undefined && <span className="tabular-nums text-muted-foreground">{nf.format(n)}</span>}
              </button>
            );
          })}
        </div>
      )}

      <div className="relative mt-4 h-[280px]">
        <ResponsiveContainer width="100%" height="100%">
          <AreaChart
            data={rows}
            margin={{ top: 8, right: 8, left: -12, bottom: 0 }}
            onClick={(state) => {
              const i = Number(state?.activeTooltipIndex);
              const row = Number.isInteger(i) ? rows[i] : undefined;
              if (row?.covered) onSelect(row.t);
            }}
            style={{ cursor: "pointer" }}
          >
            <CartesianGrid vertical={false} stroke="hsl(var(--border))" strokeOpacity={0.7} />
            <XAxis
              dataKey="t"
              tickFormatter={fmtSlot}
              tickLine={false}
              axisLine={{ stroke: "hsl(var(--border))" }}
              tick={{ fontSize: 11, fill: "hsl(var(--muted-foreground))" }}
              interval="preserveStartEnd"
              minTickGap={28}
              tickMargin={8}
            />
            <YAxis
              allowDecimals={false}
              tickLine={false}
              axisLine={false}
              width={44}
              tick={{ fontSize: 11, fill: "hsl(var(--muted-foreground))" }}
              tickFormatter={(v: number) => nf.format(v)}
            />
            <Tooltip
              cursor={{ stroke: "hsl(var(--foreground) / 0.25)", strokeWidth: 1 }}
              isAnimationActive={false}
              content={(props) => (
                <RewindTooltip
                  active={props.active}
                  row={props.payload?.[0]?.payload as ChartRow | undefined}
                  visible={visible}
                  fmt={formatDateTimeMedium}
                />
              )}
            />
            {visible.map(({ g, color }) => (
              <Area
                key={g.key}
                dataKey={g.key}
                name={g.label}
                stackId="groups"
                type="stepAfter"
                stroke={color}
                strokeWidth={1.5}
                fill={color}
                fillOpacity={0.28}
                connectNulls={false}
                isAnimationActive={false}
                activeDot={false}
                dot={(props: { cx?: number; cy?: number; index?: number }) =>
                  props.index !== undefined && isolated.has(props.index) && props.cx != null && props.cy != null ? (
                    <circle key={`${g.key}-${props.index}`} cx={props.cx} cy={props.cy} r={3.5} fill={color} stroke="hsl(var(--background))" strokeWidth={1.5} />
                  ) : (
                    <g key={`${g.key}-${props.index}`} />
                  )
                }
              />
            ))}
            {compareSlot && (
              <ReferenceLine x={compareSlot} stroke="hsl(var(--muted-foreground))" strokeDasharray="4 3" strokeWidth={1.25} />
            )}
            {selected && <ReferenceLine x={selected} stroke="hsl(var(--primary))" strokeWidth={2} />}
          </AreaChart>
        </ResponsiveContainer>
        {!anyCovered && (
          <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
            <span className="rounded-md border border-glass bg-popover px-3 py-1.5 text-xs text-muted-foreground shadow-xs">
              No snapshots in this window
            </span>
          </div>
        )}
      </div>
    </div>
  );
}

function RewindTooltip({
  active,
  row,
  visible,
  fmt,
}: {
  active?: boolean;
  row?: ChartRow;
  visible: Array<{ g: RewindGroup; color: string }>;
  fmt: (iso: string) => string;
}) {
  if (!active || !row) return null;
  const lines = visible.map(({ g, color }) => ({ g, color, n: Number(row[g.key] ?? 0) })).filter((l) => l.n > 0);
  const total = lines.reduce((acc, l) => acc + l.n, 0);
  return (
    <div className="min-w-48 rounded-(--radius) border border-glass bg-popover p-3 text-xs text-popover-foreground shadow-md">
      <div className="mb-2 font-semibold">{fmt(row.t)}</div>
      {!row.covered ? (
        <span className="text-muted-foreground">Not captured</span>
      ) : lines.length === 0 ? (
        <span className="text-muted-foreground">No tickets</span>
      ) : (
        <div className="flex flex-col gap-1">
          {lines.map(({ g, color, n }) => (
            <div key={g.key} className="flex items-center justify-between gap-4">
              <span className="flex min-w-0 items-center gap-2">
                <span className="h-2 w-2 shrink-0 rounded-[2px]" style={{ backgroundColor: color }} />
                <span className="truncate text-muted-foreground">{g.label || "All tickets"}</span>
              </span>
              <span className="tabular-nums font-medium">{nf.format(n)}</span>
            </div>
          ))}
          {lines.length > 1 && (
            <div className="mt-1 flex justify-between border-t border-glass pt-2 font-semibold">
              <span>Total</span>
              <span className="tabular-nums">{nf.format(total)}</span>
            </div>
          )}
        </div>
      )}
      {row.covered && <div className="mt-2 text-[10px] text-muted-foreground">Click to open this moment</div>}
    </div>
  );
}

// ---- snapshot list ---------------------------------------------------------------

function SummaryChip({ tone, n, label }: { tone: "new" | "gone" | "moved" | "changed"; n: number; label: string }) {
  return (
    <span
      className={cn(
        "inline-flex h-6 items-center gap-1 rounded-md border px-2 text-[11px] font-medium tabular-nums",
        n === 0 && "border-glass text-muted-foreground",
        n > 0 && tone === "new" && "border-primary/30 bg-primary/10 text-primary",
        n > 0 && tone === "gone" && "border-glass bg-glass text-foreground",
        n > 0 && tone === "moved" && "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-300",
        n > 0 && tone === "changed" && "border-sky-500/30 bg-sky-500/10 text-sky-700 dark:text-sky-300",
      )}
    >
      {nf.format(n)} {label}
    </span>
  );
}

function SnapshotList({
  snapshot,
  diff,
  compareLabel,
  compareMissing,
  colorFor,
  hidden,
  dimmed,
}: {
  snapshot: RewindSnapshot;
  diff: SnapshotDiff;
  compareLabel: string | null;
  compareMissing: boolean;
  colorFor: (g: RewindGroup) => string;
  hidden: Set<string>;
  dimmed: boolean;
}) {
  const theme = useTheme();
  const deleted = React.useMemo(() => new Set(snapshot.deletedIds), [snapshot.deletedIds]);
  const byGroup = React.useMemo(() => {
    const m = new Map<string, RewindItem[]>();
    for (const i of snapshot.items) {
      const list = m.get(i.group);
      if (list) list.push(i);
      else m.set(i.group, [i]);
    }
    return m;
  }, [snapshot.items]);
  const showHeaders = snapshot.groups.length > 1 || (snapshot.groups[0]?.label ?? "") !== "";

  return (
    <div className={cn("glass-card overflow-hidden transition-opacity", dimmed && "opacity-60")}>
      <div className="flex flex-wrap items-baseline justify-between gap-2 px-5 pb-3 pt-4">
        <h3 className="text-sm font-semibold text-foreground">
          {nf.format(snapshot.items.length)} ticket{snapshot.items.length === 1 ? "" : "s"} in the view
        </h3>
        <span className="text-xs text-muted-foreground">
          {compareLabel
            ? `Changes since ${compareLabel}`
            : compareMissing
              ? "Nothing was captured at the compare moment"
              : "No comparison"}
          {snapshot.truncated && " · list was capped at the view's page size"}
        </span>
      </div>

      {snapshot.items.length === 0 ? (
        <p className="px-5 pb-5 text-sm text-muted-foreground">The view was empty at this moment.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-sm">
            <thead>
              <tr className="border-y border-glass text-left text-[11px] uppercase tracking-[0.06em] text-muted-foreground">
                <th scope="col" className="w-12 px-5 py-2 text-right font-medium">#</th>
                <th scope="col" className="w-24 px-2 py-2 font-medium">Change</th>
                <th scope="col" className="px-3 py-2 font-medium">Ticket</th>
                <th scope="col" className="px-3 py-2 font-medium">Subject</th>
                <th scope="col" className="px-3 py-2 font-medium">Requester</th>
                <th scope="col" className="px-3 py-2 font-medium">Status</th>
                <th scope="col" className="px-5 py-2 font-medium">Assignee</th>
              </tr>
            </thead>
            {snapshot.groups.map((g) => {
              if (hidden.has(g.key)) return null;
              const items = byGroup.get(g.key) ?? [];
              const delta = diff.groupDelta.get(g.key) ?? 0;
              return (
                <tbody key={g.key} className="sd-rewind-group">
                  {showHeaders && (
                    <tr className="bg-glass">
                      <th colSpan={7} scope="rowgroup" className="px-5 py-1.5 text-left">
                        <span className="inline-flex items-center gap-2 text-xs font-semibold text-foreground">
                          <span className="h-2.5 w-2.5 rounded-[3px]" style={{ backgroundColor: colorFor(g) }} />
                          {g.label || "All tickets"}
                          <span className="font-normal tabular-nums text-muted-foreground">{nf.format(items.length)}</span>
                          {diff.hasCompare && delta !== 0 && (
                            <span
                              className={cn(
                                "font-medium tabular-nums",
                                delta > 0 ? "text-amber-700 dark:text-amber-300" : "text-emerald-700 dark:text-emerald-300",
                              )}
                            >
                              {delta > 0 ? `+${delta}` : delta}
                            </span>
                          )}
                        </span>
                      </th>
                    </tr>
                  )}
                  {items.map((item) => (
                    <SnapshotRow
                      key={item.id}
                      row={diff.rows.get(item.id)!}
                      deleted={deleted.has(item.id)}
                      theme={theme}
                    />
                  ))}
                </tbody>
              );
            })}
            {diff.gone.length > 0 && (
              <tbody>
                <tr className="bg-glass">
                  <th colSpan={7} scope="rowgroup" className="px-5 py-1.5 text-left">
                    <span className="inline-flex items-center gap-2 text-xs font-semibold text-muted-foreground">
                      No longer in the view
                      <span className="font-normal tabular-nums">{nf.format(diff.gone.length)}</span>
                    </span>
                  </th>
                </tr>
                {diff.gone.map(({ item, groupLabel }) => (
                  <tr key={item.id} className="border-b border-glass text-muted-foreground last:border-0">
                    <td className="px-5 py-2" />
                    <td className="px-2 py-2 text-[11px]">Left</td>
                    <td className="whitespace-nowrap px-3 py-2">
                      <TicketLink id={item.id} number={item.number} deleted={false} />
                    </td>
                    <td className="max-w-md truncate px-3 py-2 line-through decoration-muted-foreground/40" title={item.subject}>
                      {item.subject}
                    </td>
                    <td className="max-w-56 truncate px-3 py-2">{item.requester}</td>
                    <td className="whitespace-nowrap px-3 py-2 text-xs">{groupLabel ? `was in ${groupLabel}` : "—"}</td>
                    <td className="px-5 py-2" />
                  </tr>
                ))}
              </tbody>
            )}
          </table>
        </div>
      )}
    </div>
  );
}

function SnapshotRow({
  row,
  deleted,
  theme,
}: {
  row: DiffRow;
  deleted: boolean;
  theme: ReturnType<typeof useTheme>;
}) {
  const { item } = row;
  return (
    <tr className={cn("border-b border-glass last:border-0 hover:bg-glass-hover", row.isNew && "bg-primary/[0.04]")}>
      <td className="px-5 py-2 text-right align-top font-mono text-xs tabular-nums text-muted-foreground">{row.position}</td>
      <td className="px-2 py-2 align-top">
        <ChangeBadge row={row} />
      </td>
      <td className="whitespace-nowrap px-3 py-2 align-top">
        <TicketLink id={item.id} number={item.number} deleted={deleted} />
      </td>
      <td className="max-w-md px-3 py-2 align-top">
        <div className="flex min-w-0 items-center gap-1.5">
          {item.isCallback && <PhoneCall className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-label="Call-back" />}
          {item.isResearch && <FlaskConical className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-label="Research" />}
          <span className="truncate text-foreground" title={item.subject}>
            {item.subject}
          </span>
        </div>
        {row.changes.length > 0 && (
          <div className="mt-0.5 flex flex-col gap-0.5">
            {row.changes.map((c) => (
              <span key={c.label} className="truncate text-[11px] text-sky-700 dark:text-sky-300" title={`${c.label}: ${c.from} → ${c.to}`}>
                {c.label}: <span className="text-muted-foreground">{c.from}</span> → {c.to}
              </span>
            ))}
          </div>
        )}
      </td>
      <td className="max-w-56 px-3 py-2 align-top text-muted-foreground">
        <div className="truncate" title={item.requester}>{item.requester || "—"}</div>
        {item.companyName && (
          <div className="truncate text-[11px]" title={item.companyName}>
            {item.companyName}
          </div>
        )}
      </td>
      <td className="whitespace-nowrap px-3 py-2 align-top">
        <span className="rounded px-2 py-0.5 text-[11px] font-medium" style={colorPillStyle(item.statusColor || "#6b7280", theme)}>
          {item.statusName}
        </span>
        {!item.priorityIsDefault && (
          <span
            className="ml-1.5 rounded px-2 py-0.5 text-[11px] font-medium"
            style={colorPillStyle(item.priorityColor || "#ef4444", theme)}
          >
            {item.priorityName}
          </span>
        )}
      </td>
      <td className="max-w-48 truncate px-5 py-2 align-top text-xs text-muted-foreground" title={item.assigneeEmail ?? undefined}>
        {item.assigneeEmail ?? "—"}
      </td>
    </tr>
  );
}

function ChangeBadge({ row }: { row: DiffRow }) {
  if (row.isNew)
    return (
      <span className="inline-flex h-5 items-center rounded border border-primary/30 bg-primary/10 px-1.5 text-[10px] font-semibold uppercase tracking-wide text-primary">
        New
      </span>
    );
  if (row.fromGroup)
    return (
      <span className="block max-w-24 truncate text-[11px] text-amber-700 dark:text-amber-300" title={`From ${row.fromGroup}`}>
        from {row.fromGroup}
      </span>
    );
  if (row.moved > 0)
    return (
      <span className="inline-flex items-center gap-0.5 text-[11px] font-medium tabular-nums text-emerald-700 dark:text-emerald-300" title={`Up ${row.moved}`}>
        <ArrowUp className="h-3 w-3" />
        {row.moved}
      </span>
    );
  if (row.moved < 0)
    return (
      <span className="inline-flex items-center gap-0.5 text-[11px] font-medium tabular-nums text-amber-700 dark:text-amber-300" title={`Down ${-row.moved}`}>
        <ArrowDown className="h-3 w-3" />
        {-row.moved}
      </span>
    );
  return null;
}

function TicketLink({ id, number, deleted }: { id: string; number: number; deleted: boolean }) {
  if (deleted)
    return (
      <span className="font-mono text-xs text-muted-foreground" title="This ticket has since been deleted">
        #{number} <span className="font-sans text-[10px] uppercase tracking-wide">deleted</span>
      </span>
    );
  return (
    <Link
      to={"/tickets/$ticketId" as never}
      params={{ ticketId: id } as never}
      className="font-mono text-xs font-medium text-primary hover:underline"
    >
      #{number}
    </Link>
  );
}
