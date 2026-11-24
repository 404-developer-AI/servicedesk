import * as React from "react";
import { serverTodayIso, shiftMonth } from "@/lib/dateFormat";
import { useServerNowMs, useServerTimeZone } from "@/hooks/useServerTime";

export type YearMonth = { year: number; month: number };

/**
 * The current calendar month on the server clock in the app time zone —
 * never the browser's (project rule: server-based timing). Null until the
 * first time sync; re-evaluated per minute, so a page left open across
 * midnight rolls over.
 */
export function useServerYearMonth(): YearMonth | null {
  const tz = useServerTimeZone();
  const nowMs = useServerNowMs();
  const today = nowMs === null ? null : serverTodayIso(tz, nowMs);
  return React.useMemo(
    () => (today ? { year: Number(today.slice(0, 4)), month: Number(today.slice(5, 7)) } : null),
    [today],
  );
}

/**
 * A month picker state that follows the server's current month until the
 * user steps away from it; `reset` ("This month") follows it again. `ym` is
 * null only before the first time sync.
 */
export function useMonthCursor(): {
  ym: YearMonth | null;
  step: (delta: number) => void;
  reset: () => void;
} {
  const current = useServerYearMonth();
  const [picked, setPicked] = React.useState<YearMonth | null>(null);
  const ym = picked ?? current;
  const step = React.useCallback(
    (delta: number) => setPicked((p) => {
      const base = p ?? current;
      return base ? shiftMonth(base, delta) : p;
    }),
    [current],
  );
  const reset = React.useCallback(() => setPicked(null), []);
  return { ym, step, reset };
}
