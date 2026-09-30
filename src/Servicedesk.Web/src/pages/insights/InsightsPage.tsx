import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ChartColumnStacked, CircleCheckBig, Inbox, Users } from "lucide-react";
import { insightsApi, type InsightsConfig, type InsightsReportKind } from "@/lib/insights-api";
import { cn } from "@/lib/utils";
import { AgentActivityReportView } from "./AgentActivityReport";
import { TicketCountReportView } from "./TicketCountReport";
import { useReportFilters } from "./useReportFilters";

type InsightsTab = InsightsReportKind | "agents";

const TABS: ReadonlyArray<{ kind: InsightsTab; label: string; icon: typeof Inbox }> = [
  { kind: "new-tickets", label: "New tickets", icon: Inbox },
  { kind: "closed-tickets", label: "Closed tickets", icon: CircleCheckBig },
  { kind: "agents", label: "Agents", icon: Users },
];

// Per-viewer convenience: reopen on the last overview.
const TAB_KEY = "sd-insights-tab";

function readTab(): InsightsTab {
  try {
    const v = window.localStorage.getItem(TAB_KEY);
    return v === "closed-tickets" || v === "agents" ? v : "new-tickets";
  } catch {
    return "new-tickets";
  }
}

/// v0.1.13 — Insights: the reporting dashboard. Per-user opt-in
/// (`insights_enabled`); every figure is scoped server-side to the viewer's
/// queue access. One tab per overview; the period filter is shared.
/// v0.1.14 adds the Agents tab (per-agent work, side by side).
export function InsightsPage() {
  const config = useQuery({
    queryKey: ["insights", "config"],
    queryFn: () => insightsApi.config(),
    staleTime: 5 * 60_000,
  });

  return (
    <div className="flex flex-1 flex-col gap-6">
      <header className="flex items-end justify-between gap-4">
        <div className="space-y-1.5">
          <div className="flex items-center gap-2 text-primary">
            <ChartColumnStacked className="h-5 w-5" />
            <span className="text-[11px] font-semibold uppercase tracking-[0.14em]">Insights</span>
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Reports</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            How work flows into and out of the desk, and who does it. Every figure respects your
            queue access and every overview exports to PDF exactly as you see it.
          </p>
        </div>
      </header>

      {config.isLoading ? (
        <div className="glass-card h-[520px] animate-pulse" />
      ) : config.isError || !config.data ? (
        <div className="glass-card p-6 text-sm text-muted-foreground">
          Insights could not be loaded. Try again in a moment.
        </div>
      ) : (
        <Reports config={config.data} />
      )}
    </div>
  );
}

function Reports({ config }: { config: InsightsConfig }) {
  const filters = useReportFilters(config);
  const [tab, setTab] = React.useState<InsightsTab>(readTab);

  function choose(kind: InsightsTab) {
    setTab(kind);
    try {
      window.localStorage.setItem(TAB_KEY, kind);
    } catch {
      // storage unavailable — the tab still switches
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <div role="tablist" aria-label="Overview" className="sd-insights-tabs flex gap-1 border-b border-glass">
        {TABS.map(({ kind, label, icon: Icon }) => {
          const active = tab === kind;
          return (
            <button
              key={kind}
              type="button"
              role="tab"
              aria-selected={active}
              onClick={() => choose(kind)}
              className={cn(
                "-mb-px inline-flex items-center gap-2 border-b-2 px-3 pb-2.5 pt-1 text-sm font-medium transition-colors",
                active
                  ? "border-primary text-foreground"
                  : "border-transparent text-muted-foreground hover:text-foreground",
              )}
            >
              <Icon className="h-4 w-4" />
              {label}
            </button>
          );
        })}
      </div>

      {tab === "agents" ? (
        <AgentActivityReportView config={config} filters={filters} />
      ) : (
        <TicketCountReportView key={tab} kind={tab} filters={filters} />
      )}
    </div>
  );
}
