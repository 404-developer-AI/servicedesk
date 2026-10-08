import * as React from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Check, ChevronLeft, ChevronRight, ClipboardCheck, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { formatDateTimeMedium } from "@/lib/dateFormat";
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
  type InsightsConfig,
  type InsightsPeriod,
  type ReportParams,
  type WorkflowAgentRow,
  type WorkflowKpi,
  type WorkflowLimits,
  type WorkflowScore,
} from "@/lib/insights-api";
import { formatMinutes, nf, periodTitle } from "./insightsFormat";
import { DateInput, IconButton, Segmented } from "./InsightsControls";
import type { ReportFilters } from "./useReportFilters";

// The report window is capped server-side, so no "Year" here.
const WORKFLOW_PERIODS: ReadonlyArray<{ value: InsightsPeriod; label: string }> = [
  { value: "today", label: "Today" },
  { value: "week", label: "Week" },
  { value: "month", label: "Month" },
  { value: "custom", label: "Custom" },
];

const VIEW_KEY = "sd-insights-workflow-view";

type ScoreKpi = Exclude<WorkflowKpi, "ResearchLoad">;

const COLUMNS: ReadonlyArray<{ kpi: WorkflowKpi; title: string; needsView: boolean }> = [
  { kpi: "CallBeforeMail", title: "Call before mail", needsView: false },
  { kpi: "TemplateBeforePending", title: "Template before Pending", needsView: false },
  { kpi: "NoCherryPicking", title: "Top to bottom", needsView: true },
  { kpi: "TimeLimits", title: "Time limits", needsView: true },
  { kpi: "ResearchLoad", title: "Research load", needsView: true },
];

const SCORE_FIELD: Record<ScoreKpi, keyof Pick<WorkflowAgentRow, "callBeforeMail" | "templateBeforePending" | "noCherryPicking" | "timeLimits">> = {
  CallBeforeMail: "callBeforeMail",
  TemplateBeforePending: "templateBeforePending",
  NoCherryPicking: "noCherryPicking",
  TimeLimits: "timeLimits",
};

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

function ruleText(kpi: WorkflowKpi, l: WorkflowLimits): string {
  switch (kpi) {
    case "CallBeforeMail":
      return "Every mail to the customer is preceded by a call on the ticket (by anyone, voicemail included) after the customer's last mail.";
    case "TemplateBeforePending":
      return `Every move to Pending by the agent, or by a trigger their action set off, follows a filled-in Workflow template — or a mail within ${l.mailAfterTemplateMinutes} min after one.`;
    case "NoCherryPicking":
      return "High prio, Call-back and WFP are taken top to bottom: no ticket above was left that nobody had time registered on at that moment. Research may be picked freely.";
    case "TimeLimits":
      return `Registered time per ticket per group: High prio ${l.priorityMinutes} min, Call-back ${l.callbackMinutes}, WFP ${l.wfpMinutes}, Research ${l.researchMinutes}. Over it, Call-back/WFP move to Research and High prio/Research get a Specialist Consult.`;
    case "ResearchLoad":
      return `At most ${l.maxResearchPerAgent} Research tickets assigned to one agent at the same time.`;
  }
}

/// v0.1.32 — Workflow: the servicedesk work order, checked per agent.
/// Managers and admins only (server-enforced); opening an agent's cases is
/// audited.
export function WorkflowReportView({ config, filters }: { config: InsightsConfig; filters: ReportFilters }) {
  const wf = useQuery({
    queryKey: ["insights", "workflow", "config"],
    queryFn: () => insightsApi.workflowConfig(),
    staleTime: 5 * 60_000,
  });

  if (wf.isLoading) return <div className="glass-card h-[480px] animate-pulse" />;
  if (wf.isError || !wf.data)
    return (
      <div className="glass-card p-6 text-sm text-muted-foreground">
        {insightsErrorMessage(wf.error) ?? "Workflow could not be loaded. Try again in a moment."}
      </div>
    );
  return <Workflow config={config} filters={filters} views={wf.data.views} limits={wf.data.limits} maxDays={wf.data.maxDays} />;
}

function Workflow({
  config,
  filters,
  views,
  limits,
  maxDays,
}: {
  config: InsightsConfig;
  filters: ReportFilters;
  views: Array<{ id: string; name: string }>;
  limits: WorkflowLimits;
  maxDays: number;
}) {
  const { period, setPeriod, offset, setOffset, customFrom, setCustomFrom, customTo, setCustomTo } = filters;
  // "year" may have been picked on another tab; the window is capped here.
  const effectivePeriod: InsightsPeriod = period === "year" ? "month" : period;

  const [viewId, setViewId] = React.useState<string | null>(() => {
    const stored = readPref(VIEW_KEY);
    return views.some((v) => v.id === stored) ? stored : views[0]?.id ?? null;
  });
  const [open, setOpen] = React.useState<{ agentId: string; kpi: WorkflowKpi } | null>(null);

  const customValid = !!customFrom && !!customTo && customFrom <= customTo;
  const params: ReportParams = {
    period: effectivePeriod,
    offset,
    from: effectivePeriod === "custom" ? customFrom : undefined,
    to: effectivePeriod === "custom" ? customTo : undefined,
  };

  const query = useQuery({
    queryKey: ["insights", "workflow", params, viewId],
    queryFn: () => insightsApi.workflow(params, viewId),
    enabled: effectivePeriod !== "custom" || customValid,
    placeholderData: keepPreviousData,
  });
  const report = query.data;
  const errorMessage = query.isError ? insightsErrorMessage(query.error) : null;

  const openRow = open && report?.agents.find((a) => a.agentId === open.agentId);

  return (
    <section className="flex flex-col gap-4" aria-labelledby="insights-workflow">
      {/* ---- filter row ---------------------------------------------------- */}
      <div className="glass-card sd-insights-filters flex flex-wrap items-center gap-3 p-3">
        <Segmented
          ariaLabel="Period"
          value={effectivePeriod}
          options={WORKFLOW_PERIODS}
          onChange={(v) => {
            setPeriod(v as InsightsPeriod);
            setOpen(null);
          }}
        />
        {effectivePeriod !== "custom" ? (
          <div className="flex items-center gap-1">
            <IconButton label="Previous period" onClick={() => setOffset((o) => o - 1)} disabled={offset <= -120}>
              <ChevronLeft className="h-4 w-4" />
            </IconButton>
            <span className="min-w-40 px-1 text-center text-sm font-medium tabular-nums text-foreground">
              {report ? periodTitle(report.range, effectivePeriod) : "…"}
            </span>
            <IconButton label="Next period" onClick={() => setOffset((o) => Math.min(0, o + 1))} disabled={offset >= 0}>
              <ChevronRight className="h-4 w-4" />
            </IconButton>
          </div>
        ) : (
          <div className="flex items-center gap-2">
            <DateInput label="From" value={customFrom} max={customTo || config.today} onChange={setCustomFrom} />
            <span className="text-xs text-muted-foreground">to</span>
            <DateInput label="To" value={customTo} min={customFrom || undefined} max={config.today} onChange={setCustomTo} />
          </div>
        )}

        <div className="ml-auto flex items-center gap-2">
          <span className="text-xs text-muted-foreground">Tracked view</span>
          {views.length > 0 ? (
            <Select
              value={viewId ?? ""}
              onValueChange={(v) => {
                setViewId(v);
                writePref(VIEW_KEY, v);
                setOpen(null);
              }}
            >
              <SelectTrigger className="h-8 w-56 text-sm" aria-label="Tracked view">
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
          ) : (
            <span className="text-xs text-muted-foreground">none — turn on Track in Rewind for a view</span>
          )}
        </div>
      </div>

      {effectivePeriod === "custom" && !customValid && (
        <p className="text-xs text-muted-foreground">Pick a start date on or before the end date (at most {maxDays} days).</p>
      )}
      {errorMessage && <p className="text-xs text-destructive">{errorMessage}</p>}

      {/* ---- scores --------------------------------------------------------- */}
      {!report ? (
        query.isError ? null : <div className="glass-card h-[360px] animate-pulse" />
      ) : (
        <div className={cn("glass-card overflow-hidden transition-opacity", query.isPlaceholderData && "opacity-60")}>
          <div className="flex flex-wrap items-start justify-between gap-3 px-5 pb-3 pt-4">
            <div>
              <h2 id="insights-workflow" className="text-base font-semibold text-foreground">
                Work order per agent
              </h2>
              <p className="max-w-3xl text-xs text-muted-foreground">
                Each score is checks passed out of checks made in this period. Click a score to see every case. Only
                tickets in your queues are counted; opening an agent's cases is logged.
              </p>
            </div>
          </div>

          {report.agents.length === 0 ? (
            <p className="px-5 pb-6 text-sm text-muted-foreground">Nothing to check in this period.</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-sm">
                <thead>
                  <tr className="border-y border-glass text-left text-[11px] uppercase tracking-[0.06em] text-muted-foreground">
                    <th scope="col" className="px-5 py-2 font-medium">Agent</th>
                    {COLUMNS.map((c) => (
                      <th key={c.kpi} scope="col" className="px-3 py-2 font-medium" title={ruleText(c.kpi, limits)}>
                        {c.title}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {report.agents.map((a) => (
                    <tr key={a.agentId} className="border-b border-glass last:border-0">
                      <th scope="row" className="max-w-56 truncate px-5 py-2.5 text-left font-medium text-foreground" title={a.name}>
                        {a.name}
                      </th>
                      {COLUMNS.map((c) => {
                        const active = open?.agentId === a.agentId && open.kpi === c.kpi;
                        const disabled = c.needsView && !viewId;
                        return (
                          <td key={c.kpi} className="px-2 py-1.5">
                            {c.kpi === "ResearchLoad" ? (
                              <ResearchCell
                                minutes={a.researchOverMinutes}
                                peak={a.researchPeak}
                                active={active}
                                disabled={disabled}
                                onClick={() => setOpen(active ? null : { agentId: a.agentId, kpi: c.kpi })}
                              />
                            ) : (
                              <ScoreCell
                                score={a[SCORE_FIELD[c.kpi]]}
                                active={active}
                                disabled={disabled}
                                onClick={() => setOpen(active ? null : { agentId: a.agentId, kpi: c.kpi })}
                              />
                            )}
                          </td>
                        );
                      })}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {open && openRow && (
        <CaseList
          params={params}
          viewId={viewId}
          agentId={open.agentId}
          agentName={openRow.name}
          kpi={open.kpi}
          rule={ruleText(open.kpi, limits)}
          onClose={() => setOpen(null)}
        />
      )}

      {/* ---- the rules ------------------------------------------------------- */}
      <div className="glass-card p-5">
        <h3 className="mb-3 text-sm font-semibold text-foreground">What is checked</h3>
        <dl className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {COLUMNS.map((c) => (
            <div key={c.kpi} className="space-y-0.5">
              <dt className="text-xs font-medium text-foreground">{c.title}</dt>
              <dd className="text-xs text-muted-foreground">{ruleText(c.kpi, limits)}</dd>
            </div>
          ))}
        </dl>
        <p className="mt-3 text-[11px] text-muted-foreground">
          Top to bottom, time limits and research load read the tracked view's Rewind snapshots and are recorded from
          the moment a view is tracked. Limits and the WFP statuses are set under Settings → General → Insights.
        </p>
      </div>
    </section>
  );
}

function tone(pct: number) {
  if (pct >= 90) return "text-emerald-700 dark:text-emerald-300";
  if (pct >= 70) return "text-amber-700 dark:text-amber-300";
  return "text-red-700 dark:text-red-300";
}

function barTone(pct: number) {
  if (pct >= 90) return "bg-emerald-500/70";
  if (pct >= 70) return "bg-amber-500/70";
  return "bg-red-500/70";
}

/// A cell with nothing to open (no checks / never over the limit).
function CellStatic({ children }: { children: React.ReactNode }) {
  return <div className="flex w-full min-w-28 flex-col gap-1 px-3 py-2">{children}</div>;
}

function CellButton({
  active,
  disabled,
  onClick,
  children,
  label,
}: {
  active: boolean;
  disabled: boolean;
  onClick: () => void;
  children: React.ReactNode;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-pressed={active}
      aria-label={label}
      className={cn(
        "flex w-full min-w-28 flex-col gap-1 rounded-md border px-3 py-2 text-left transition-colors",
        active ? "border-primary/50 bg-primary/10" : "border-transparent hover:border-glass hover:bg-glass-hover",
        "disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:border-transparent disabled:hover:bg-transparent",
      )}
    >
      {children}
    </button>
  );
}

function ScoreCell({
  score,
  active,
  disabled,
  onClick,
}: {
  score: WorkflowScore;
  active: boolean;
  disabled: boolean;
  onClick: () => void;
}) {
  if (score.total === 0)
    return (
      <CellStatic>
        <span className="text-sm text-muted-foreground">—</span>
        <span className="text-[11px] text-muted-foreground">{disabled ? "no tracked view" : "no checks"}</span>
      </CellStatic>
    );
  const pct = Math.round((score.ok / score.total) * 100);
  return (
    <CellButton active={active} disabled={disabled} onClick={onClick} label={`${score.ok} of ${score.total} passed`}>
      <span className="flex items-baseline gap-2">
        <span className={cn("text-base font-semibold tabular-nums", tone(pct))} style={{ fontVariationSettings: '"opsz" 32' }}>
          {pct}%
        </span>
        <span className="text-[11px] tabular-nums text-muted-foreground">
          {nf.format(score.ok)}/{nf.format(score.total)}
        </span>
      </span>
      <span className="h-1 w-full overflow-hidden rounded-full bg-glass-strong">
        <span className={cn("block h-full rounded-full", barTone(pct))} style={{ width: `${pct}%` }} />
      </span>
    </CellButton>
  );
}

function ResearchCell({
  minutes,
  peak,
  active,
  disabled,
  onClick,
}: {
  minutes: number;
  peak: number;
  active: boolean;
  disabled: boolean;
  onClick: () => void;
}) {
  if (minutes === 0)
    return (
      <CellStatic>
        {disabled ? (
          <span className="text-sm text-muted-foreground">—</span>
        ) : (
          <span className="text-sm font-medium text-emerald-700 dark:text-emerald-300">Within limit</span>
        )}
        <span className="text-[11px] text-muted-foreground">{disabled ? "no tracked view" : "never over"}</span>
      </CellStatic>
    );
  return (
    <CellButton active={active} disabled={disabled} onClick={onClick} label={`Over the limit for ${minutes} minutes`}>
      <span className="text-sm font-semibold tabular-nums text-red-700 dark:text-red-300">{formatMinutes(minutes)} over</span>
      <span className="text-[11px] tabular-nums text-muted-foreground">peak {peak} at once</span>
    </CellButton>
  );
}

function CaseList({
  params,
  viewId,
  agentId,
  agentName,
  kpi,
  rule,
  onClose,
}: {
  params: ReportParams;
  viewId: string | null;
  agentId: string;
  agentName: string;
  kpi: WorkflowKpi;
  rule: string;
  onClose: () => void;
}) {
  const [onlyProblems, setOnlyProblems] = React.useState(true);
  const query = useQuery({
    queryKey: ["insights", "workflow", "cases", params, viewId, agentId, kpi],
    queryFn: () => insightsApi.workflowCases(params, viewId, agentId, kpi),
    staleTime: 60_000,
  });
  const items = (query.data?.items ?? []).filter((c) => !onlyProblems || !c.ok);
  const problems = (query.data?.items ?? []).filter((c) => !c.ok).length;
  const title = COLUMNS.find((c) => c.kpi === kpi)?.title ?? kpi;

  return (
    <div className="glass-card overflow-hidden">
      <div className="flex flex-wrap items-start justify-between gap-3 px-5 pb-3 pt-4">
        <div className="min-w-0">
          <div className="flex items-center gap-2 text-primary">
            <ClipboardCheck className="h-4 w-4" />
            <h3 className="text-sm font-semibold text-foreground">
              {agentName} · {title}
            </h3>
          </div>
          <p className="mt-0.5 max-w-3xl text-xs text-muted-foreground">{rule}</p>
        </div>
        <div className="flex items-center gap-2">
          {kpi !== "ResearchLoad" && (
            <Segmented
              ariaLabel="Show"
              value={onlyProblems ? "problems" : "all"}
              options={[
                { value: "problems", label: `Problems${query.data ? ` (${problems})` : ""}` },
                { value: "all", label: `All${query.data ? ` (${query.data.total})` : ""}` },
              ]}
              onChange={(v) => setOnlyProblems(v === "problems")}
            />
          )}
          <IconButton label="Close" onClick={onClose}>
            <X className="h-4 w-4" />
          </IconButton>
        </div>
      </div>

      {query.isLoading ? (
        <div className="mx-5 mb-5 h-40 animate-pulse rounded-md bg-glass" />
      ) : query.isError ? (
        <p className="px-5 pb-5 text-sm text-muted-foreground">
          {insightsErrorMessage(query.error) ?? "The cases could not be loaded."}
        </p>
      ) : items.length === 0 ? (
        <p className="px-5 pb-5 text-sm text-muted-foreground">
          {onlyProblems ? "No problems in this period." : "No checks in this period."}
        </p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-sm">
            <thead>
              <tr className="border-y border-glass text-left text-[11px] uppercase tracking-[0.06em] text-muted-foreground">
                <th scope="col" className="w-10 px-5 py-2 font-medium" />
                <th scope="col" className="px-3 py-2 font-medium">When</th>
                <th scope="col" className="px-3 py-2 font-medium">Ticket</th>
                <th scope="col" className="px-3 py-2 font-medium">Subject</th>
                <th scope="col" className="px-5 py-2 font-medium">What happened</th>
              </tr>
            </thead>
            <tbody>
              {items.map((c, i) => (
                <tr key={`${c.ticketId ?? "x"}-${c.atUtc}-${i}`} className="border-b border-glass last:border-0 hover:bg-glass-hover">
                  <td className="px-5 py-2 align-top">
                    {c.ok ? (
                      <Check className="h-4 w-4 text-emerald-600 dark:text-emerald-400" aria-label="Passed" />
                    ) : (
                      <X className="h-4 w-4 text-red-600 dark:text-red-400" aria-label="Not passed" />
                    )}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 align-top text-xs tabular-nums text-muted-foreground">
                    {formatDateTimeMedium(c.atUtc)}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 align-top">
                    {c.ticketId && c.ticketNumber != null ? (
                      <Link
                        to={"/tickets/$ticketId" as never}
                        params={{ ticketId: c.ticketId } as never}
                        className="font-mono text-xs font-medium text-primary hover:underline"
                      >
                        #{c.ticketNumber}
                      </Link>
                    ) : (
                      <span className="text-xs text-muted-foreground">—</span>
                    )}
                  </td>
                  <td className="max-w-sm truncate px-3 py-2 align-top text-foreground" title={c.subject ?? undefined}>
                    {c.subject ?? "—"}
                  </td>
                  <td className="px-5 py-2 align-top text-xs text-muted-foreground">{c.detail}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {query.data && query.data.items.length < query.data.total && (
            <p className="border-t border-glass px-5 py-2 text-[11px] text-muted-foreground">
              Showing the first {nf.format(query.data.items.length)} of {nf.format(query.data.total)} cases — pick a shorter
              period to see them all.
            </p>
          )}
        </div>
      )}
    </div>
  );
}
