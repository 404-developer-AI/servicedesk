import * as React from "react";
import {
  ALL_OUTCOMES,
  type ClosedOutcome,
  type InsightsConfig,
  type InsightsGranularity,
  type InsightsPeriod,
} from "@/lib/insights-api";

/// Filter state shared by every Insights overview, owned by the page so the
/// period survives switching between tabs (compare new vs closed for the
/// same month without re-picking it).
export type ReportFilters = {
  period: InsightsPeriod;
  setPeriod: (p: InsightsPeriod) => void;
  offset: number;
  setOffset: React.Dispatch<React.SetStateAction<number>>;
  customFrom: string;
  setCustomFrom: (v: string) => void;
  customTo: string;
  setCustomTo: (v: string) => void;
  granularity: InsightsGranularity | undefined;
  setGranularity: (g: InsightsGranularity | undefined) => void;
  outcomes: readonly ClosedOutcome[];
  toggleOutcome: (o: ClosedOutcome) => void;
};

export function useReportFilters(config: InsightsConfig): ReportFilters {
  const [period, setPeriodState] = React.useState<InsightsPeriod>(config.defaultPeriod);
  const [offset, setOffset] = React.useState(0);
  const [customFrom, setCustomFrom] = React.useState(`${config.today.slice(0, 8)}01`);
  const [customTo, setCustomTo] = React.useState(config.today);
  const [granularity, setGranularity] = React.useState<InsightsGranularity | undefined>();
  const [outcomes, setOutcomes] = React.useState<readonly ClosedOutcome[]>(ALL_OUTCOMES);

  const setPeriod = React.useCallback((p: InsightsPeriod) => {
    setPeriodState(p);
    setOffset(0);
    setGranularity(undefined);
  }, []);

  // At least one outcome always stays on — an empty selection has no
  // meaning and the server would refuse it.
  const toggleOutcome = React.useCallback((o: ClosedOutcome) => {
    setOutcomes((prev) => {
      if (prev.includes(o)) return prev.length === 1 ? prev : prev.filter((x) => x !== o);
      return ALL_OUTCOMES.filter((x) => x === o || prev.includes(x));
    });
  }, []);

  return {
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
  };
}
