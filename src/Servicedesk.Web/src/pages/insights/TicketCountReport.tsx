import * as React from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import {
  Bar,
  BarChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, Download, Loader2 } from "lucide-react";
import { toast } from "sonner";
import { useTheme } from "@/app/ThemeProvider";
import { cn } from "@/lib/utils";
import {
  insightsApi,
  insightsErrorMessage,
  type ClosedOutcome,
  type InsightsGranularity,
  type InsightsPeriod,
  type InsightsQueue,
  type InsightsReportKind,
  type ReportParams,
  type TicketCountBucket,
} from "@/lib/insights-api";
import {
  axisLabel,
  bucketLabel,
  daysBetweenInclusive,
  isPartial,
  nf,
  periodTitle,
  slotColor,
} from "./insightsFormat";
import { TicketList } from "./TicketList";
import type { ReportFilters } from "./useReportFilters";

const PERIODS: ReadonlyArray<{ value: InsightsPeriod; label: string }> = [
  { value: "today", label: "Today" },
  { value: "week", label: "Week" },
  { value: "month", label: "Month" },
  { value: "year", label: "Year" },
  { value: "custom", label: "Custom" },
];

/** Groupings that make sense per period (finer than the period itself). */
const GRANULARITIES: Record<InsightsPeriod, InsightsGranularity[]> = {
  today: ["day"],
  week: ["day"],
  month: ["day", "week"],
  year: ["day", "week", "month"],
  custom: ["day", "week", "month", "year"],
};

const OUTCOME_LABEL: Record<ClosedOutcome, string> = {
  resolved: "Resolved",
  closed: "Closed",
  merged: "Merged",
};

/** Per-overview wording. */
const KIND_TEXT: Record<InsightsReportKind, { title: string; description: string }> = {
  "new-tickets": {
    title: "New tickets",
    description: "Counted on the day a ticket was created. Stacked per queue — click a queue to show or hide it. Deleted tickets are not counted.",
  },
  "closed-tickets": {
    title: "Closed tickets",
    description: "Counted on the moment a ticket was resolved, closed or merged, for tickets that are still in that state (a reopened ticket is open again). Stacked per queue — click a queue to show or hide it. Deleted tickets are not counted.",
  },
};

const GRANULARITY_LABEL: Record<InsightsGranularity, string> = {
  day: "Day",
  week: "Week",
  month: "Month",
  year: "Year",
};

// Per-viewer convenience: remember which queues were switched off.
const HIDDEN_KEY = "sd-insights-hidden-queues";

function readHidden(): Set<string> {
  try {
    const raw = window.localStorage.getItem(HIDDEN_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    return new Set(Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === "string") : []);
  } catch {
    return new Set();
  }
}

function writeHidden(ids: Set<string>) {
  try {
    window.localStorage.setItem(HIDDEN_KEY, JSON.stringify([...ids]));
  } catch {
    // storage unavailable — the toggle still works for this visit
  }
}

type ChartRow = { key: string; bucket: TicketCountBucket; top: string | null } & Record<string, unknown>;

type SortKey = { col: "period" | "total" | string; dir: "asc" | "desc" };

/// One ticket-count overview (new or closed). Filter state lives on the
/// page (shared across tabs); queue visibility is shared too, via storage.
export function TicketCountReportView({
  kind,
  filters,
}: {
  kind: InsightsReportKind;
  filters: ReportFilters;
}) {
  const { mode } = useTheme();
  const dark = mode === "dark";
  const text = KIND_TEXT[kind];

  const {
    period,
    setPeriod,
    offset,
    setOffset,
    customFrom,
    setCustomFrom,
    customTo,
    setCustomTo,
    granularity,
    setGranularity,
    outcomes,
    toggleOutcome,
  } = filters;
  const [hidden, setHidden] = React.useState<Set<string>>(readHidden);
  const [exporting, setExporting] = React.useState(false);

  const customValid = !!customFrom && !!customTo && customFrom <= customTo;
  const params: ReportParams = {
    period,
    offset,
    from: period === "custom" ? customFrom : undefined,
    to: period === "custom" ? customTo : undefined,
    granularity,
    outcomes: kind === "closed-tickets" ? outcomes : undefined,
  };

  const query = useQuery({
    queryKey: ["insights", kind, params],
    queryFn: () => insightsApi.report(kind, params),
    placeholderData: keepPreviousData,
    enabled: period !== "custom" || customValid,
    staleTime: 30_000,
    retry: false,
  });
  const report = query.data;
  const g = report?.granularity ?? "day";

  function choosePeriod(p: InsightsPeriod) {
    setPeriod(p);
  }

  function toggleQueue(id: string) {
    setHidden((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      writeHidden(next);
      return next;
    });
  }

  function onlyQueue(id: string) {
    if (!report) return;
    const next = new Set(report.queues.filter((q) => q.id !== id).map((q) => q.id));
    writeHidden(next);
    setHidden(next);
  }

  function showAll() {
    const next = new Set<string>();
    writeHidden(next);
    setHidden(next);
  }

  // Visible queues keep their original index so counts stay aligned.
  const visible = React.useMemo(
    () =>
      (report?.queues ?? [])
        .map((q, index) => ({ q, index }))
        .filter(({ q }) => !hidden.has(q.id)),
    [report, hidden],
  );

  const queueTotals = React.useMemo(() => {
    const totals = new Map<string, number>();
    for (const q of report?.queues ?? []) totals.set(q.id, 0);
    report?.buckets.forEach((b) =>
      report.queues.forEach((q, i) => totals.set(q.id, (totals.get(q.id) ?? 0) + b.counts[i])),
    );
    return totals;
  }, [report]);

  const rows: ChartRow[] = React.useMemo(
    () =>
      (report?.buckets ?? []).map((b) => {
        const row: ChartRow = { key: b.start, bucket: b, top: null };
        for (const { q, index } of visible) {
          row[q.id] = b.counts[index];
          if (b.counts[index] > 0) row.top = q.id;
        }
        return row;
      }),
    [report, visible],
  );

  const summary = React.useMemo(() => {
    if (!report) return null;
    let total = 0;
    let peak: TicketCountBucket | null = null;
    let peakCount = 0;
    for (const b of report.buckets) {
      const sum = visible.reduce((acc, { index }) => acc + b.counts[index], 0);
      total += sum;
      if (sum > peakCount) {
        peakCount = sum;
        peak = b;
      }
    }
    const lastCounted = report.to < report.today ? report.to : report.today;
    const elapsed = lastCounted < report.from ? 0 : daysBetweenInclusive(report.from, lastCounted);
    let top: InsightsQueue | null = null;
    let topCount = 0;
    for (const { q } of visible) {
      const t = queueTotals.get(q.id) ?? 0;
      if (t > topCount) {
        topCount = t;
        top = q;
      }
    }
    return { total, elapsed, avg: elapsed ? total / elapsed : 0, peak, peakCount, top, topCount };
  }, [report, visible, queueTotals]);

  async function exportPdf() {
    if (!report || visible.length === 0) return;
    setExporting(true);
    try {
      const allVisible = visible.length === report.queues.length;
      await insightsApi.downloadPdf(
        kind,
        { ...params, granularity: report.granularity },
        allVisible ? [] : visible.map(({ q }) => q.id),
      );
    } catch (err) {
      toast.error(insightsErrorMessage(err) ?? "Could not create the PDF.");
    } finally {
      setExporting(false);
    }
  }

  const errorMessage = query.isError ? insightsErrorMessage(query.error) : null;
  const refetching = query.isFetching && !!report;

  return (
    <section className="flex flex-col gap-4" aria-labelledby={`insights-${kind}`}>
      {/* ---- filter row ---------------------------------------------------- */}
      <div className="glass-card sd-insights-filters flex flex-wrap items-center gap-3 p-3">
        <Segmented
          ariaLabel="Period"
          value={period}
          options={PERIODS}
          onChange={(v) => choosePeriod(v as InsightsPeriod)}
        />

        {period !== "custom" ? (
          <div className="flex items-center gap-1">
            <IconButton label="Previous period" onClick={() => setOffset((o) => o - 1)} disabled={offset <= -120}>
              <ChevronLeft className="h-4 w-4" />
            </IconButton>
            <span className="min-w-[10rem] px-1 text-center text-sm font-medium tabular-nums text-foreground">
              {report ? periodTitle(report, period) : "…"}
            </span>
            <IconButton label="Next period" onClick={() => setOffset((o) => Math.min(0, o + 1))} disabled={offset >= 0}>
              <ChevronRight className="h-4 w-4" />
            </IconButton>
          </div>
        ) : (
          <div className="flex items-center gap-2">
            <DateInput label="From" value={customFrom} max={customTo || undefined} onChange={setCustomFrom} />
            <span className="text-xs text-muted-foreground">to</span>
            <DateInput label="To" value={customTo} min={customFrom || undefined} onChange={setCustomTo} />
          </div>
        )}

        {kind === "closed-tickets" && (
          <div className="flex items-center gap-2">
            <span className="text-xs text-muted-foreground">Outcome</span>
            <div role="group" aria-label="Outcome" className="sd-segmented inline-flex rounded-md border border-glass bg-glass p-0.5">
              {(Object.keys(OUTCOME_LABEL) as ClosedOutcome[]).map((o) => {
                const on = outcomes.includes(o);
                return (
                  <button
                    key={o}
                    type="button"
                    aria-pressed={on}
                    onClick={() => toggleOutcome(o)}
                    title={on && outcomes.length === 1 ? "At least one outcome stays selected" : undefined}
                    className={cn(
                      "h-7 rounded-[5px] px-3 text-xs font-medium transition-colors",
                      on ? "bg-background text-foreground shadow-sm" : "text-muted-foreground hover:text-foreground",
                    )}
                  >
                    {OUTCOME_LABEL[o]}
                  </button>
                );
              })}
            </div>
          </div>
        )}

        <div className="ml-auto flex flex-wrap items-center gap-3">
          {GRANULARITIES[period].length > 1 && (
            <div className="flex items-center gap-2">
              <span className="text-xs text-muted-foreground">Group by</span>
              <Segmented
                ariaLabel="Group by"
                value={granularity ?? report?.granularity ?? ""}
                options={GRANULARITIES[period].map((v) => ({ value: v, label: GRANULARITY_LABEL[v] }))}
                onChange={(v) => setGranularity(v as InsightsGranularity)}
              />
            </div>
          )}
          <button
            type="button"
            onClick={exportPdf}
            disabled={!report || visible.length === 0 || exporting}
            className="sd-insights-export inline-flex h-8 items-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {exporting ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Download className="h-3.5 w-3.5" />}
            Export PDF
          </button>
        </div>
      </div>

      {period === "custom" && !customValid && (
        <p className="text-xs text-muted-foreground">Pick a start date on or before the end date.</p>
      )}
      {errorMessage && <p className="text-xs text-destructive">{errorMessage}</p>}

      {!report ? (
        query.isError ? (
          <div className="glass-card p-6 text-sm text-muted-foreground">
            {errorMessage ?? "The report could not be loaded."}
          </div>
        ) : (
          <div className="glass-card h-[460px] animate-pulse" />
        )
      ) : report.queues.length === 0 ? (
        <div className="glass-card p-6 text-sm text-muted-foreground">
          You don't have access to any queues, so there is nothing to report on yet.
        </div>
      ) : (
        <div className={cn("flex flex-col gap-4 transition-opacity", refetching && "opacity-60")}>
          {/* ---- KPI tiles --------------------------------------------------- */}
          {summary && (
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
              <Kpi label={text.title} value={nf.format(summary.total)} sub={periodTitle(report, period)} />
              <Kpi
                label="Average per day"
                value={summary.avg.toFixed(1)}
                sub={summary.elapsed ? `over ${summary.elapsed} day${summary.elapsed === 1 ? "" : "s"}` : "period not started"}
              />
              <Kpi
                label={`Busiest ${g}`}
                value={summary.peak ? nf.format(summary.peakCount) : "—"}
                sub={summary.peak ? bucketLabel(summary.peak.start, g) : "no tickets"}
              />
              <Kpi
                label="Largest queue"
                value={summary.top?.name ?? "—"}
                valueSize="text"
                swatch={summary.top ? slotColor(summary.top.slot, dark) : undefined}
                sub={
                  summary.top && summary.total > 0
                    ? `${nf.format(summary.topCount)} tickets · ${Math.round((summary.topCount / summary.total) * 100)}%`
                    : undefined
                }
              />
            </div>
          )}

          {/* ---- chart ------------------------------------------------------- */}
          <div className="glass-card p-5">
            <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
              <div>
                <h2 id={`insights-${kind}`} className="text-base font-semibold text-foreground">
                  {text.title} per {g}
                </h2>
                <p className="max-w-3xl text-xs text-muted-foreground">{text.description}</p>
              </div>
            </div>

            <QueueToggles
              queues={report.queues}
              hidden={hidden}
              totals={queueTotals}
              dark={dark}
              onToggle={toggleQueue}
              onOnly={onlyQueue}
              onAll={showAll}
            />

            <div className="relative mt-4 h-[320px]">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={rows} margin={{ top: 8, right: 4, left: -12, bottom: 0 }} barCategoryGap="24%">
                  <CartesianGrid vertical={false} stroke="hsl(var(--border))" strokeOpacity={0.7} />
                  <XAxis
                    dataKey="key"
                    tickFormatter={(v: string) => axisLabel(v, g)}
                    tickLine={false}
                    axisLine={{ stroke: "hsl(var(--border))" }}
                    tick={{ fontSize: 11, fill: "hsl(var(--muted-foreground))" }}
                    interval="preserveStartEnd"
                    minTickGap={18}
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
                    cursor={{ fill: "hsl(var(--foreground) / 0.05)", radius: 6 }}
                    content={(props) => (
                      <ChartTooltip
                        active={props.active}
                        row={props.payload?.[0]?.payload as ChartRow | undefined}
                        granularity={g}
                        visible={visible}
                        dark={dark}
                      />
                    )}
                    isAnimationActive={false}
                  />
                  {visible.map(({ q }) => (
                    <Bar
                      key={q.id}
                      dataKey={q.id}
                      name={q.name}
                      stackId="tickets"
                      fill={slotColor(q.slot, dark)}
                      isAnimationActive={false}
                      shape={(props: SegmentProps) => <Segment {...props} isTop={props.payload?.top === q.id} />}
                    />
                  ))}
                </BarChart>
              </ResponsiveContainer>
              {summary && summary.total === 0 && (
                <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
                  <span className="rounded-md border border-glass bg-popover px-3 py-1.5 text-xs text-muted-foreground shadow-sm">
                    {visible.length === 0 ? "All queues are switched off" : `No ${text.title.toLowerCase()} in this period`}
                  </span>
                </div>
              )}
            </div>
          </div>

          {/* ---- table ------------------------------------------------------- */}
          <BreakdownTable
            buckets={report.buckets}
            granularity={g}
            today={report.today}
            visible={visible}
            dark={dark}
          />

          {/* ---- tickets behind the numbers ---------------------------------- */}
          <TicketList
            kind={kind}
            params={{ ...params, granularity: undefined }}
            queueIds={visible.length === report.queues.length ? [] : visible.map(({ q }) => q.id)}
            disabled={visible.length === 0}
          />
        </div>
      )}
    </section>
  );
}

// ---- pieces ---------------------------------------------------------------

function Segmented({
  ariaLabel,
  value,
  options,
  onChange,
}: {
  ariaLabel: string;
  value: string;
  options: ReadonlyArray<{ value: string; label: string }>;
  onChange: (v: string) => void;
}) {
  return (
    <div role="radiogroup" aria-label={ariaLabel} className="sd-segmented inline-flex rounded-md border border-glass bg-glass p-0.5">
      {options.map((o) => {
        const active = o.value === value;
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={active}
            onClick={() => onChange(o.value)}
            className={cn(
              "h-7 rounded-[5px] px-3 text-xs font-medium transition-colors",
              active
                ? "bg-background text-foreground shadow-sm"
                : "text-muted-foreground hover:text-foreground",
            )}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}

function IconButton({
  label,
  onClick,
  disabled,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      disabled={disabled}
      className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-glass bg-glass text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
    >
      {children}
    </button>
  );
}

function DateInput({
  label,
  value,
  min,
  max,
  onChange,
}: {
  label: string;
  value: string;
  min?: string;
  max?: string;
  onChange: (v: string) => void;
}) {
  return (
    <input
      type="date"
      aria-label={label}
      value={value}
      min={min}
      max={max}
      onChange={(e) => onChange(e.target.value)}
      className="h-8 rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-none focus:border-primary/60"
    />
  );
}

function Kpi({
  label,
  value,
  sub,
  swatch,
  valueSize = "number",
}: {
  label: string;
  value: string;
  sub?: string;
  swatch?: string;
  valueSize?: "number" | "text";
}) {
  return (
    <div className="glass-card flex min-w-0 flex-col gap-1 p-5">
      <span className="text-[11px] font-medium uppercase tracking-[0.08em] text-muted-foreground">{label}</span>
      <div className="flex min-w-0 items-center gap-2">
        {swatch && <span className="h-2.5 w-2.5 shrink-0 rounded-[3px]" style={{ backgroundColor: swatch }} />}
        <span
          className={cn(
            "truncate font-semibold tracking-tight text-foreground",
            valueSize === "number" ? "text-[28px] leading-9" : "text-lg leading-9",
          )}
          style={valueSize === "number" ? { fontVariationSettings: '"opsz" 32' } : undefined}
          title={value}
        >
          {value}
        </span>
      </div>
      {sub && <span className="truncate text-xs text-muted-foreground">{sub}</span>}
    </div>
  );
}

function QueueToggles({
  queues,
  hidden,
  totals,
  dark,
  onToggle,
  onOnly,
  onAll,
}: {
  queues: InsightsQueue[];
  hidden: Set<string>;
  totals: Map<string, number>;
  dark: boolean;
  onToggle: (id: string) => void;
  onOnly: (id: string) => void;
  onAll: () => void;
}) {
  const anyHidden = queues.some((q) => hidden.has(q.id));
  return (
    <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Queues">
      {queues.map((q) => {
        const on = !hidden.has(q.id);
        const color = slotColor(q.slot, dark);
        return (
          <div
            key={q.id}
            className={cn(
              "group sd-queue-chip inline-flex items-center rounded-full border text-xs transition-colors",
              on ? "border-glass bg-glass" : "border-dashed border-glass bg-transparent",
            )}
          >
            <button
              type="button"
              aria-pressed={on}
              onClick={() => onToggle(q.id)}
              className={cn(
                "inline-flex h-7 items-center gap-2 rounded-full pl-2.5 pr-2 transition-colors",
                on ? "text-foreground" : "text-muted-foreground",
              )}
            >
              <span
                className="h-2.5 w-2.5 rounded-[3px] border-2"
                style={{ backgroundColor: on ? color : "transparent", borderColor: color }}
              />
              <span className={cn("max-w-[14rem] truncate", !on && "line-through decoration-muted-foreground/40")}>
                {q.name}
              </span>
              <span className="tabular-nums text-muted-foreground">{nf.format(totals.get(q.id) ?? 0)}</span>
            </button>
            <button
              type="button"
              onClick={() => onOnly(q.id)}
              className="mr-1 hidden h-5 rounded-full px-1.5 text-[10px] font-medium uppercase tracking-wide text-muted-foreground hover:bg-glass-hover hover:text-foreground group-hover:inline-flex group-focus-within:inline-flex items-center"
              title={`Show only ${q.name}`}
            >
              Only
            </button>
          </div>
        );
      })}
      {anyHidden && (
        <button
          type="button"
          onClick={onAll}
          className="h-7 rounded-full px-2.5 text-xs font-medium text-primary hover:underline"
        >
          Show all
        </button>
      )}
    </div>
  );
}

type SegmentProps = {
  x?: number;
  y?: number;
  width?: number;
  height?: number;
  fill?: string;
  payload?: ChartRow;
};

/// Stacked segment with a 2px surface gap above interior segments and a
/// rounded data-end on the topmost one — no borders around marks.
function Segment({ x = 0, y = 0, width = 0, height = 0, fill, isTop }: SegmentProps & { isTop: boolean }) {
  if (!height || height <= 0 || width <= 0) return null;
  const gap = isTop ? 0 : Math.min(2, height / 2);
  const top = y + gap;
  const h = height - gap;
  if (!isTop) return <rect x={x} y={top} width={width} height={h} fill={fill} />;
  const r = Math.min(4, width / 2, h);
  const d = `M${x},${top + h} V${top + r} Q${x},${top} ${x + r},${top} H${x + width - r} Q${x + width},${top} ${x + width},${top + r} V${top + h} Z`;
  return <path d={d} fill={fill} />;
}

function ChartTooltip({
  active,
  row,
  granularity,
  visible,
  dark,
}: {
  active?: boolean;
  row?: ChartRow;
  granularity: InsightsGranularity;
  visible: Array<{ q: InsightsQueue; index: number }>;
  dark: boolean;
}) {
  if (!active || !row) return null;
  const b = row.bucket;
  const lines = visible
    .map(({ q, index }) => ({ q, n: b.counts[index] }))
    .filter((l) => l.n > 0)
    .reverse(); // top of the stack first
  const total = lines.reduce((acc, l) => acc + l.n, 0);
  return (
    <div className="min-w-[12rem] rounded-[var(--radius)] border border-glass bg-popover p-3 text-xs text-popover-foreground shadow-md">
      <div className="mb-2 flex items-baseline justify-between gap-4">
        <span className="font-semibold">{bucketLabel(b.start, granularity)}</span>
        {isPartial(b, granularity) && <span className="text-[10px] text-muted-foreground">partial</span>}
      </div>
      {lines.length === 0 ? (
        <span className="text-muted-foreground">No new tickets</span>
      ) : (
        <div className="flex flex-col gap-1">
          {lines.map(({ q, n }) => (
            <div key={q.id} className="flex items-center justify-between gap-4">
              <span className="flex min-w-0 items-center gap-2">
                <span className="h-2 w-2 shrink-0 rounded-[2px]" style={{ backgroundColor: slotColor(q.slot, dark) }} />
                <span className="truncate text-muted-foreground">{q.name}</span>
              </span>
              <span className="tabular-nums font-medium">{nf.format(n)}</span>
            </div>
          ))}
        </div>
      )}
      {lines.length > 1 && (
        <div className="mt-2 flex justify-between border-t border-glass pt-2 font-semibold">
          <span>Total</span>
          <span className="tabular-nums">{nf.format(total)}</span>
        </div>
      )}
    </div>
  );
}

function BreakdownTable({
  buckets,
  granularity,
  today,
  visible,
  dark,
}: {
  buckets: TicketCountBucket[];
  granularity: InsightsGranularity;
  today: string;
  visible: Array<{ q: InsightsQueue; index: number }>;
  dark: boolean;
}) {
  const [sort, setSort] = React.useState<SortKey>({ col: "period", dir: "asc" });

  const rows = React.useMemo(() => {
    const withTotals = buckets.map((b) => ({
      b,
      total: visible.reduce((acc, { index }) => acc + b.counts[index], 0),
    }));
    const dir = sort.dir === "asc" ? 1 : -1;
    const valueOf = (r: (typeof withTotals)[number]) => {
      if (sort.col === "period") return r.b.start;
      if (sort.col === "total") return r.total;
      const v = visible.find(({ q }) => q.id === sort.col);
      return v ? r.b.counts[v.index] : 0;
    };
    return [...withTotals].sort((a, c) => {
      const va = valueOf(a);
      const vc = valueOf(c);
      if (va === vc) return a.b.start < c.b.start ? -1 : 1;
      return (va < vc ? -1 : 1) * dir;
    });
  }, [buckets, visible, sort]);

  const colTotals = visible.map(({ index }) => buckets.reduce((acc, b) => acc + b.counts[index], 0));
  const grand = colTotals.reduce((a, b) => a + b, 0);

  function clickSort(col: string) {
    setSort((s) =>
      s.col === col
        ? { col, dir: s.dir === "asc" ? "desc" : "asc" }
        : { col, dir: col === "period" ? "asc" : "desc" },
    );
  }

  return (
    <div className="glass-card overflow-hidden">
      <div className="max-h-[480px] overflow-auto">
        <table className="w-full border-collapse text-sm">
          <thead className="sticky top-0 z-10 bg-popover">
            <tr className="border-b border-glass">
              <SortHeader label={GRANULARITY_LABEL[granularity]} col="period" sort={sort} onSort={clickSort} align="left" />
              {visible.map(({ q }) => (
                <SortHeader
                  key={q.id}
                  label={q.name}
                  col={q.id}
                  sort={sort}
                  onSort={clickSort}
                  swatch={slotColor(q.slot, dark)}
                />
              ))}
              <SortHeader label="Total" col="total" sort={sort} onSort={clickSort} strong />
            </tr>
          </thead>
          <tbody>
            {rows.map(({ b, total }) => {
              const future = b.from > today;
              return (
                <tr key={b.start} className={cn("border-b border-glass last:border-0", future && "opacity-45")}>
                  <td className="whitespace-nowrap px-4 py-2 text-foreground">
                    {bucketLabel(b.start, granularity)}
                    {isPartial(b, granularity) && (
                      <span className="ml-2 text-[10px] uppercase tracking-wide text-muted-foreground">partial</span>
                    )}
                  </td>
                  {visible.map(({ q, index }) => (
                    <td key={q.id} className="px-4 py-2 text-right tabular-nums">
                      {b.counts[index] === 0 ? (
                        <span className="text-muted-foreground/50">·</span>
                      ) : (
                        nf.format(b.counts[index])
                      )}
                    </td>
                  ))}
                  <td className="px-4 py-2 text-right font-semibold tabular-nums text-foreground">{nf.format(total)}</td>
                </tr>
              );
            })}
          </tbody>
          <tfoot className="sticky bottom-0 bg-popover">
            <tr className="border-t border-glass font-semibold">
              <td className="px-4 py-2.5 text-foreground">Total</td>
              {colTotals.map((t, i) => (
                <td key={visible[i].q.id} className="px-4 py-2.5 text-right tabular-nums">
                  {nf.format(t)}
                </td>
              ))}
              <td className="px-4 py-2.5 text-right tabular-nums text-primary">{nf.format(grand)}</td>
            </tr>
          </tfoot>
        </table>
      </div>
    </div>
  );
}

function SortHeader({
  label,
  col,
  sort,
  onSort,
  align = "right",
  swatch,
  strong,
}: {
  label: string;
  col: string;
  sort: SortKey;
  onSort: (col: string) => void;
  align?: "left" | "right";
  swatch?: string;
  strong?: boolean;
}) {
  const active = sort.col === col;
  return (
    <th
      scope="col"
      aria-sort={active ? (sort.dir === "asc" ? "ascending" : "descending") : "none"}
      className={cn("px-4 py-2.5 font-medium", align === "left" ? "text-left" : "text-right")}
    >
      <button
        type="button"
        onClick={() => onSort(col)}
        className={cn(
          "inline-flex max-w-[12rem] items-center gap-1.5 text-[11px] uppercase tracking-[0.06em] transition-colors hover:text-foreground",
          active || strong ? "text-foreground" : "text-muted-foreground",
        )}
      >
        {swatch && <span className="h-2 w-2 shrink-0 rounded-[2px]" style={{ backgroundColor: swatch }} />}
        <span className="truncate" title={label}>{label}</span>
        {active &&
          (sort.dir === "asc" ? <ArrowUp className="h-3 w-3 shrink-0" /> : <ArrowDown className="h-3 w-3 shrink-0" />)}
      </button>
    </th>
  );
}
