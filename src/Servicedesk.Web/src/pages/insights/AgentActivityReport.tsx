import * as React from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { ChevronLeft, ChevronRight, Download, Info, Loader2, Users } from "lucide-react";
import { toast } from "sonner";
import { useTheme } from "@/app/ThemeProvider";
import { useAuth } from "@/auth/authStore";
import { cn } from "@/lib/utils";
import {
  insightsApi,
  insightsErrorMessage,
  type AgentActivityBucket,
  type AgentActivityTotals,
  type AgentChartMetric,
  type AgentTicketSort,
  type InsightsAgent,
  type InsightsConfig,
  type InsightsGranularity,
  type InsightsPeriod,
  type ReportParams,
} from "@/lib/insights-api";
import { AgentMultiPicker } from "./AgentMultiPicker";
import { AgentTicketList } from "./AgentTicketList";
import { DateInput, IconButton, Segmented } from "./InsightsControls";
import { GRANULARITIES, GRANULARITY_LABEL, PERIODS, axisLabel, bucketLabel, formatMinutes, formatSeconds, isPartial, nf, periodTitle, slotColor } from "./insightsFormat";
import type { ReportFilters } from "./useReportFilters";

const METRICS: ReadonlyArray<{ value: AgentChartMetric; label: string }> = [
  { value: "tickets", label: "Tickets" },
  { value: "time", label: "Time" },
  { value: "calls", label: "Calls" },
];

const METRIC_TITLE: Record<AgentChartMetric, string> = {
  tickets: "Tickets worked on",
  time: "Hours on tickets",
  calls: "Calls",
};

// Per-viewer conveniences: the last comparison and chart metric.
const SELECTION_KEY = "sd-insights-agents";
const METRIC_KEY = "sd-insights-agents-metric";

function readSelection(): string[] | null {
  try {
    const raw = window.localStorage.getItem(SELECTION_KEY);
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === "string") : null;
  } catch {
    return null;
  }
}

function writeSelection(ids: string[]) {
  try {
    window.localStorage.setItem(SELECTION_KEY, JSON.stringify(ids));
  } catch {
    // storage unavailable — the selection still applies for this visit
  }
}

function readMetric(): AgentChartMetric {
  try {
    const v = window.localStorage.getItem(METRIC_KEY);
    return v === "time" || v === "calls" ? v : "tickets";
  } catch {
    return "tickets";
  }
}

function chartValue(v: AgentActivityTotals, metric: AgentChartMetric): number {
  if (metric === "time") return v.ticketMinutes / 60;
  if (metric === "calls") return v.calls;
  return v.tickets;
}

function formatMetric(v: AgentActivityTotals, metric: AgentChartMetric): string {
  if (metric === "time") return formatMinutes(v.ticketMinutes);
  if (metric === "calls") return nf.format(v.calls);
  return nf.format(v.tickets);
}

type ChartRow = { key: string; bucket: AgentActivityBucket } & Record<string, unknown>;

/// v0.1.14 — the Agents overview: one to `maxAgents` agents side by side.
/// Ticket figures respect the viewer's queue access (server-side); calls
/// are the answered calls the Telavox integration recorded.
export function AgentActivityReportView({ config, filters }: { config: InsightsConfig; filters: ReportFilters }) {
  const { mode } = useTheme();
  const dark = mode === "dark";
  const { user } = useAuth();
  const max = Math.max(1, config.maxAgents);

  const { period, setPeriod, offset, setOffset, customFrom, setCustomFrom, customTo, setCustomTo, granularity, setGranularity } =
    filters;

  // First visit: compare yourself. Afterwards the last selection returns,
  // trimmed if an admin lowered the maximum since.
  const [selected, setSelectedState] = React.useState<string[]>(
    () => (readSelection() ?? (user ? [user.id] : [])).slice(0, max),
  );
  const [metric, setMetricState] = React.useState<AgentChartMetric>(readMetric);
  const [sort, setSort] = React.useState<AgentTicketSort>("recent");
  const [exporting, setExporting] = React.useState(false);

  function setSelected(ids: string[]) {
    const next = ids.slice(0, max);
    writeSelection(next);
    setSelectedState(next);
  }

  function setMetric(m: AgentChartMetric) {
    try {
      window.localStorage.setItem(METRIC_KEY, m);
    } catch {
      // storage unavailable
    }
    setMetricState(m);
  }

  const colorFor = React.useCallback((slot: number) => slotColor(slot, dark), [dark]);

  const customValid = !!customFrom && !!customTo && customFrom <= customTo;
  const params: ReportParams = {
    period,
    offset,
    from: period === "custom" ? customFrom : undefined,
    to: period === "custom" ? customTo : undefined,
    granularity,
  };

  const query = useQuery({
    queryKey: ["insights", "agents", params, selected],
    queryFn: () => insightsApi.agents(params, selected),
    placeholderData: keepPreviousData,
    enabled: selected.length > 0 && (period !== "custom" || customValid),
    staleTime: 30_000,
    retry: false,
  });
  // A kept-previous result for a different selection would mislabel the
  // columns — show the loading state until the new comparison arrives.
  const sameSelection =
    !!query.data &&
    query.data.agents.length === selected.length &&
    query.data.agents.every((a, i) => a.id === selected[i]);
  const report = sameSelection ? query.data : undefined;
  const g: InsightsGranularity = report?.granularity ?? "day";

  const names = React.useMemo(() => {
    const m = new Map<string, string>();
    for (const a of query.data?.agents ?? []) m.set(a.id, a.name);
    return m;
  }, [query.data]);

  const rows: ChartRow[] = React.useMemo(
    () =>
      (report?.buckets ?? []).map((b) => {
        const row: ChartRow = { key: b.start, bucket: b };
        report?.agents.forEach((a, i) => {
          row[a.id] = chartValue(b.values[i], metric);
        });
        return row;
      }),
    [report, metric],
  );
  const chartEmpty = !report || rows.every((r) => report.agents.every((a) => !r[a.id]));

  async function exportPdf() {
    if (!report) return;
    setExporting(true);
    try {
      await insightsApi.downloadAgentsPdf({ ...params, granularity: report.granularity }, selected, metric, sort);
    } catch (err) {
      toast.error(insightsErrorMessage(err) ?? "Could not create the PDF.");
    } finally {
      setExporting(false);
    }
  }

  const errorMessage = query.isError ? insightsErrorMessage(query.error) : null;
  const refetching = query.isFetching && !!report;

  return (
    <section className="flex flex-col gap-4" aria-labelledby="insights-agents">
      {/* ---- filter row ---------------------------------------------------- */}
      <div className="glass-card sd-insights-filters flex flex-col gap-3 p-3">
        <div className="flex flex-wrap items-center gap-3">
          <Segmented ariaLabel="Period" value={period} options={PERIODS} onChange={(v) => setPeriod(v as InsightsPeriod)} />

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
              disabled={!report || exporting}
              className="sd-insights-export inline-flex h-8 items-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {exporting ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Download className="h-3.5 w-3.5" />}
              Export PDF
            </button>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-3 border-t border-glass pt-3">
          <span className="text-xs text-muted-foreground">Agents</span>
          <AgentMultiPicker selected={selected} max={max} names={names} colorFor={colorFor} onChange={setSelected} />
        </div>
      </div>

      {period === "custom" && !customValid && (
        <p className="text-xs text-muted-foreground">Pick a start date on or before the end date.</p>
      )}
      {errorMessage && <p className="text-xs text-destructive">{errorMessage}</p>}

      {selected.length === 0 ? (
        <div className="glass-card flex flex-col items-center gap-2 px-6 py-14 text-center">
          <div className="rounded-full bg-glass p-3 text-primary">
            <Users className="h-5 w-5" />
          </div>
          <p className="text-sm font-medium text-foreground">Pick an agent to get started</p>
          <p className="max-w-md text-xs text-muted-foreground">
            See which tickets someone worked on, how much time they logged and how many calls they
            handled — or pick up to {max} agents to compare them side by side.
          </p>
        </div>
      ) : !report ? (
        query.isError ? (
          <div className="glass-card p-6 text-sm text-muted-foreground">{errorMessage ?? "The report could not be loaded."}</div>
        ) : (
          <div className="glass-card h-[460px] animate-pulse" />
        )
      ) : (
        <div className={cn("flex flex-col gap-4 transition-opacity", refetching && "opacity-60")}>
          {/* ---- side-by-side totals ----------------------------------------- */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-5">
            <CompareTile
              label="Tickets worked on"
              agents={report.agents}
              values={report.totals.map((t) => t.tickets)}
              format={(n) => nf.format(n)}
              colorFor={colorFor}
            />
            <CompareTile
              label="Time on tickets"
              agents={report.agents}
              values={report.totals.map((t) => t.ticketMinutes)}
              format={formatMinutes}
              colorFor={colorFor}
            />
            <CompareTile
              label="Calls"
              agents={report.agents}
              values={report.totals.map((t) => t.calls)}
              format={(n) => nf.format(n)}
              detail={(i) => {
                const t = report.totals[i];
                return `${nf.format(t.callsIn)} in · ${nf.format(t.callsOut)} out`;
              }}
              colorFor={colorFor}
            />
            <CompareTile
              label="Talk time"
              agents={report.agents}
              values={report.totals.map((t) => t.callSeconds)}
              format={formatSeconds}
              detail={(i) => {
                const t = report.totals[i];
                return t.calls > 0 ? `avg ${formatSeconds(Math.round(t.callSeconds / t.calls))} per call` : "no calls";
              }}
              colorFor={colorFor}
            />
            <CompareTile
              label="Opened without action"
              agents={report.agents}
              values={report.totals.map((t) => t.openedNoAction)}
              format={(n) => nf.format(n)}
              detail={() => "opened and closed, nothing done"}
              colorFor={colorFor}
            />
          </div>

          {/* ---- chart ------------------------------------------------------- */}
          <div className="glass-card p-5">
            <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
              <div>
                <h2 id="insights-agents" className="text-base font-semibold text-foreground">
                  {METRIC_TITLE[metric]} per {g}
                </h2>
                <p className="max-w-3xl text-xs text-muted-foreground">
                  {metric === "tickets"
                    ? "A ticket counts when the agent changed it (status, queue, assignment, priority, …), added a note or reply, sent mail on it, or logged time on it. A ticket worked on over several days counts once in the totals."
                    : metric === "time"
                      ? "Time logged on tickets in the timesheet, per day it was logged."
                      : "Completed calls per agent, incoming and outgoing together."}
                </p>
              </div>
              <Segmented ariaLabel="Chart metric" value={metric} options={METRICS} onChange={(v) => setMetric(v as AgentChartMetric)} />
            </div>

            <div className="flex flex-wrap items-center gap-4 text-xs text-muted-foreground">
              {report.agents.map((a) => (
                <span key={a.id} className="inline-flex items-center gap-2">
                  <span className="h-2.5 w-2.5 rounded-[3px]" style={{ backgroundColor: colorFor(a.slot) }} />
                  <span className="text-foreground">{a.name}</span>
                  <span className="tabular-nums">{formatMetric(report.totals[a.slot], metric)}</span>
                </span>
              ))}
            </div>

            <div className="relative mt-4 h-[300px]">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={rows} margin={{ top: 8, right: 4, left: -12, bottom: 0 }} barCategoryGap="22%" barGap={2}>
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
                    allowDecimals={metric === "time"}
                    tickLine={false}
                    axisLine={false}
                    width={44}
                    tick={{ fontSize: 11, fill: "hsl(var(--muted-foreground))" }}
                    tickFormatter={(v: number) => (metric === "time" ? `${nf.format(Math.round(v * 10) / 10)}h` : nf.format(v))}
                  />
                  <Tooltip
                    cursor={{ fill: "hsl(var(--foreground) / 0.05)", radius: 6 }}
                    content={(props) => (
                      <AgentTooltip
                        active={props.active}
                        row={props.payload?.[0]?.payload as ChartRow | undefined}
                        granularity={g}
                        agents={report.agents}
                        metric={metric}
                        colorFor={colorFor}
                      />
                    )}
                    isAnimationActive={false}
                  />
                  {report.agents.map((a) => (
                    <Bar
                      key={a.id}
                      dataKey={a.id}
                      name={a.name}
                      fill={colorFor(a.slot)}
                      radius={[4, 4, 0, 0]}
                      maxBarSize={28}
                      isAnimationActive={false}
                    />
                  ))}
                </BarChart>
              </ResponsiveContainer>
              {chartEmpty && (
                <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
                  <span className="rounded-md border border-glass bg-popover px-3 py-1.5 text-xs text-muted-foreground shadow-sm">
                    Nothing recorded in this period
                  </span>
                </div>
              )}
            </div>

            <p className="mt-3 flex items-start gap-1.5 text-[11px] text-muted-foreground">
              <Info className="mt-px h-3 w-3 shrink-0" />
              Calls are the answered calls the Telavox integration saw, kept as long as the activity feed
              keeps its history. Missed calls and agents without a Telavox link are not counted. Ticket
              figures only include tickets in queues you can access.
            </p>
          </div>

          {/* ---- tickets per agent ------------------------------------------- */}
          <AgentTicketList
            agents={report.agents}
            totals={report.totals}
            params={{ ...params, granularity: undefined }}
            sort={sort}
            onSort={setSort}
            openedMinSeconds={config.openedMinSeconds}
            colorFor={colorFor}
          />
        </div>
      )}
    </section>
  );
}

/// One figure for every selected agent. A single agent gets the hero
/// number; several get a row each with a bar scaled to the largest value,
/// so the comparison reads at a glance.
function CompareTile({
  label,
  agents,
  values,
  format,
  detail,
  colorFor,
}: {
  label: string;
  agents: InsightsAgent[];
  values: number[];
  format: (n: number) => string;
  detail?: (index: number) => string;
  colorFor: (slot: number) => string;
}) {
  const top = Math.max(0, ...values);
  return (
    <div className="glass-card sd-compare-tile flex min-w-0 flex-col gap-2 p-5">
      <span className="text-[11px] font-medium uppercase tracking-[0.08em] text-muted-foreground">{label}</span>
      {agents.length === 1 ? (
        <>
          <span
            className="truncate text-[28px] font-semibold leading-9 tracking-tight text-foreground"
            style={{ fontVariationSettings: '"opsz" 32' }}
          >
            {format(values[0] ?? 0)}
          </span>
          {detail && <span className="truncate text-xs text-muted-foreground">{detail(0)}</span>}
        </>
      ) : (
        <div className="flex flex-col gap-2.5">
          {agents.map((a, i) => {
            const v = values[i] ?? 0;
            const pct = top > 0 ? Math.max(v > 0 ? 3 : 0, (v / top) * 100) : 0;
            return (
              <div key={a.id} className="flex min-w-0 flex-col gap-1">
                <div className="flex min-w-0 items-baseline justify-between gap-3">
                  <span className="truncate text-xs text-muted-foreground" title={a.name}>{a.name}</span>
                  <span className="shrink-0 text-sm font-semibold tabular-nums text-foreground">{format(v)}</span>
                </div>
                <div className="h-1.5 overflow-hidden rounded-full bg-glass">
                  <div className="h-full rounded-full" style={{ width: `${pct}%`, backgroundColor: colorFor(a.slot) }} />
                </div>
                {detail && <span className="truncate text-[11px] text-muted-foreground/80">{detail(i)}</span>}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function AgentTooltip({
  active,
  row,
  granularity,
  agents,
  metric,
  colorFor,
}: {
  active?: boolean;
  row?: ChartRow;
  granularity: InsightsGranularity;
  agents: InsightsAgent[];
  metric: AgentChartMetric;
  colorFor: (slot: number) => string;
}) {
  if (!active || !row) return null;
  const b = row.bucket;
  return (
    <div className="min-w-[13rem] rounded-[var(--radius)] border border-glass bg-popover p-3 text-xs text-popover-foreground shadow-md">
      <div className="mb-2 flex items-baseline justify-between gap-4">
        <span className="font-semibold">{bucketLabel(b.start, granularity)}</span>
        {isPartial(b, granularity) && <span className="text-[10px] text-muted-foreground">partial</span>}
      </div>
      <div className="flex flex-col gap-1">
        {agents.map((a, i) => {
          const v = b.values[i];
          return (
            <div key={a.id} className="flex items-center justify-between gap-4">
              <span className="flex min-w-0 items-center gap-2">
                <span className="h-2 w-2 shrink-0 rounded-[2px]" style={{ backgroundColor: colorFor(a.slot) }} />
                <span className="truncate text-muted-foreground">{a.name}</span>
              </span>
              <span className="tabular-nums font-medium">
                {formatMetric(v, metric)}
                {metric === "calls" && v.calls > 0 && (
                  <span className="ml-1 font-normal text-muted-foreground">({v.callsIn} in · {v.callsOut} out)</span>
                )}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}
