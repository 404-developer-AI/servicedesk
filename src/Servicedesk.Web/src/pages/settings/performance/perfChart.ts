import * as React from "react";
import { useTheme } from "@/app/ThemeProvider";
import { slotColor } from "@/pages/insights/insightsFormat";
import type { RouteRow } from "@/lib/perf-api";

/// Chart colours come from the validated Insights palette (fixed slots,
/// CVD-checked, re-stepped for the dark surface) — never cycled.
export function usePalette() {
  const { mode } = useTheme();
  const dark = mode === "dark";
  return React.useMemo(
    () => ({
      dark,
      slot: (i: number) => slotColor(i, dark),
      muted: dark ? "rgba(255,255,255,0.08)" : "rgba(28,25,23,0.06)",
      mutedStrong: dark ? "rgba(255,255,255,0.18)" : "rgba(28,25,23,0.16)",
    }),
    [dark],
  );
}

export const axisTick = { fontSize: 11, fill: "hsl(var(--muted-foreground))" } as const;
export const gridStroke = "hsl(var(--border))";

/// The four places a request's server time goes, in fixed palette slots.
export function useSplitColors() {
  const pal = usePalette();
  return React.useMemo(
    () => ({
      db: pal.slot(0),
      ext: pal.slot(1),
      pipeline: pal.slot(3),
      app: pal.slot(2),
    }),
    [pal],
  );
}

export function splitParts(r: Pick<RouteRow, "avgDbMs" | "avgExtMs" | "avgPipelineMs" | "avgAppMs">, c: ReturnType<typeof useSplitColors>) {
  return [
    { key: "db", label: "Database", value: r.avgDbMs, color: c.db },
    { key: "ext", label: "External APIs", value: r.avgExtMs, color: c.ext },
    { key: "pipeline", label: "Pipeline", value: r.avgPipelineMs, color: c.pipeline },
    { key: "app", label: "Own code", value: r.avgAppMs, color: c.app },
  ];
}

