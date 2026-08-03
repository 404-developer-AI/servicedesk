import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, Download } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";
import {
  timesheetManagerApi,
  timesheetPreferencesApi,
  formatDuration,
  formatHHMM,
  type MonthDayRollup,
  type TimesheetPreferences,
  type TimesheetUser,
} from "@/lib/timesheet-api";

/// Tab 3 — month-per-agent rollup with target comparison + CSV export.
///
/// Targets and the "working days" set are not yet a setting (commit E
/// lands those), so v0.0.35-D ships with the documented defaults
/// (8h/day, Mon–Fri). They're surfaced as `DEFAULT_*` constants here so
/// the swap to a settings-driven source is a one-line change later.
export function TimesheetTab3() {
  const usersQuery = useQuery({
    queryKey: ["timesheet", "manager", "users"],
    queryFn: () => timesheetManagerApi.listUsers(),
    staleTime: 60_000,
  });
  const users = usersQuery.data ?? [];

  const [userId, setUserId] = React.useState<string>("");
  const [{ year, month }, setYM] = React.useState(() => {
    const d = new Date();
    return { year: d.getFullYear(), month: d.getMonth() + 1 };
  });

  // Default to the first user as soon as the list loads.
  React.useEffect(() => {
    if (!userId && users.length > 0) setUserId(users[0].id);
  }, [userId, users]);

  const monthQuery = useQuery({
    queryKey: ["timesheet", "manager", "month", userId, year, month],
    queryFn: () => timesheetManagerApi.getMonth(userId, year, month),
    enabled: !!userId,
  });

  // v0.0.35-E — per-user effective preferences. While the request is in
  // flight we fall back to the v0.0.35-D hardcoded defaults so the grid
  // never flickers between two target values.
  const prefsQuery = useQuery({
    queryKey: ["timesheet", "manager", "preferences", userId],
    queryFn: () => timesheetPreferencesApi.forUser(userId),
    enabled: !!userId,
    staleTime: 60_000,
  });
  const prefs = prefsQuery.data ?? FALLBACK_PREFS;

  const data = monthQuery.data;
  const totals = computeTotals(data?.days ?? [], prefs);

  const goMonth = (delta: number) => {
    let y = year;
    let m = month + delta;
    if (m < 1) {
      m = 12;
      y -= 1;
    } else if (m > 12) {
      m = 1;
      y += 1;
    }
    setYM({ year: y, month: m });
  };

  const exportUrl = userId
    ? timesheetManagerApi.exportCsvUrl(userId, year, month)
    : "#";

  return (
    <div className="flex flex-col gap-4">
      <div className="glass-panel flex flex-wrap items-center justify-between gap-3 p-3">
        <div className="flex items-center gap-3">
          <UserPicker value={userId} onChange={setUserId} users={users} />
          <div className="flex items-center gap-1">
            <Button
              size="sm"
              variant="ghost"
              onClick={() => goMonth(-1)}
              className="h-8 w-8 p-0"
              aria-label="Previous month"
            >
              <ChevronLeft className="h-4 w-4" />
            </Button>
            <MonthLabel year={year} month={month} />
            <Button
              size="sm"
              variant="ghost"
              onClick={() => goMonth(1)}
              className="h-8 w-8 p-0"
              aria-label="Next month"
            >
              <ChevronRight className="h-4 w-4" />
            </Button>
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                const d = new Date();
                setYM({ year: d.getFullYear(), month: d.getMonth() + 1 });
              }}
              className="h-8 px-2 text-xs"
            >
              This month
            </Button>
          </div>
        </div>
        <div className="flex items-center gap-3 text-xs text-muted-foreground">
          <Badge className="border border-glass bg-glass font-normal">
            Work {formatDuration(totals.work)}
          </Badge>
          <Badge className="border border-amber-400/30 bg-amber-400/10 font-normal text-amber-200">
            Absence {formatDuration(totals.absence)}
          </Badge>
          <a
            href={exportUrl}
            className={cn(
              "inline-flex items-center gap-1 rounded-md border border-glass bg-glass px-2 py-1 text-xs text-foreground hover:bg-glass-hover",
              !userId && "pointer-events-none opacity-40",
            )}
            aria-disabled={!userId}
          >
            <Download className="h-3.5 w-3.5" />
            Export CSV
          </a>
        </div>
      </div>

      <section className="glass-card overflow-hidden">
        <div className="overflow-x-auto">
        <table className="w-full min-w-[1100px] text-left text-sm">
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="w-28 px-3 py-2 font-medium">Date</th>
              <th className="w-24 px-3 py-2 font-medium">Day</th>
              <th className="w-20 px-3 py-2 font-medium">Login</th>
              <th className="w-24 px-3 py-2 font-medium">First clock</th>
              <th className="w-24 px-3 py-2 font-medium">Last clock</th>
              <th className="px-3 py-2 font-medium">Tasks</th>
              <th className="w-28 px-3 py-2 font-medium">Absence</th>
              <th className="w-28 px-3 py-2 font-medium">Work</th>
              <th className="w-28 px-3 py-2 font-medium">vs target</th>
            </tr>
          </thead>
          <tbody>
            {monthQuery.isLoading && (
              <tr>
                <td colSpan={9} className="px-3 py-3">
                  <Skeleton className="h-6 w-full" />
                </td>
              </tr>
            )}
            {!monthQuery.isLoading && data && renderWeeks(data.days, prefs)}
          </tbody>
          {!monthQuery.isLoading && data && (
            <tfoot>
              <tr className="border-t border-glass bg-glass">
                <td className="px-3 py-2 text-xs font-medium uppercase tracking-wider text-muted-foreground">
                  Month
                </td>
                <td />
                <td />
                <td />
                <td />
                <td />
                <td className="px-3 py-2 font-mono text-xs text-amber-200">
                  {formatDuration(totals.absence)}
                </td>
                <td className="px-3 py-2 font-mono text-xs text-foreground">
                  {formatDuration(totals.work)}
                </td>
                <td className="px-3 py-2 font-mono text-xs text-muted-foreground">
                  {totals.workingDays > 0
                    ? `${formatDuration(totals.work)} / ${formatDuration(totals.workingDays * prefs.targetMinutesPerDay)}`
                    : "—"}
                </td>
              </tr>
            </tfoot>
          )}
        </table>
        </div>
      </section>
    </div>
  );
}

// ---- Per-row rendering, weekly subtotal rows --------------------------

function renderWeeks(
  days: MonthDayRollup[],
  prefs: TimesheetPreferences,
): React.ReactNode[] {
  // Group consecutive days into ISO weeks (Mon..Sun). When the week
  // changes we emit a subtotal row before continuing.
  const workDaysJs = isoWorkDaysToJsSet(prefs.workDays);
  const maxAbsence = prefs.maxAbsenceMinutesPerDay;
  const out: React.ReactNode[] = [];
  let currentWeek = -1;
  let weekWork = 0;
  let weekAbsence = 0;
  let weekWorkingDays = 0;
  // v0.0.36 — if any day in the ISO week has absence-minutes above the
  // configured ceiling, the entire week flips to "target not met"
  // regardless of total time logged. 0 = no ceiling configured.
  let weekAbsenceOvershoot = false;

  const emitWeekTotal = (anchorKey: string) => {
    if (currentWeek === -1) return;
    const failed = weekAbsenceOvershoot;
    out.push(
      <tr
        key={`wk-${anchorKey}`}
        className={cn(
          "border-b border-glass",
          failed ? "bg-red-500/[0.06]" : "bg-glass",
        )}
      >
        <td
          colSpan={6}
          className="px-3 py-1.5 text-[10px] font-medium uppercase tracking-wider text-muted-foreground"
        >
          Week {currentWeek} subtotal
          {failed && (
            <span
              className="ml-2 inline-flex items-center rounded-md border border-red-400/40 bg-red-400/10 px-1.5 py-0.5 text-[10px] font-medium text-red-200"
              title={`Absence exceeded ${formatDuration(maxAbsence)} on at least one day`}
            >
              Target not met
            </span>
          )}
        </td>
        <td
          className={cn(
            "px-3 py-1.5 font-mono text-xs",
            failed ? "text-red-200" : "text-amber-200/80",
          )}
        >
          {formatDuration(weekAbsence)}
        </td>
        <td className="px-3 py-1.5 font-mono text-xs text-foreground">
          {formatDuration(weekWork)}
        </td>
        <td className="px-3 py-1.5 font-mono text-xs text-muted-foreground">
          {weekWorkingDays > 0
            ? `${formatDuration(weekWork)} / ${formatDuration(prefs.targetMinutesPerWeek)}`
            : "—"}
        </td>
      </tr>,
    );
    weekWork = 0;
    weekAbsence = 0;
    weekWorkingDays = 0;
    weekAbsenceOvershoot = false;
  };

  for (const d of days) {
    const date = new Date(d.date + "T00:00:00");
    const isoWeek = isoWeekNumber(date);
    if (currentWeek !== -1 && isoWeek !== currentWeek) {
      emitWeekTotal(d.date);
    }
    currentWeek = isoWeek;
    const dayOvershoot = maxAbsence > 0 && d.absenceMinutes > maxAbsence;
    out.push(
      <DayRow
        key={d.date}
        day={d}
        date={date}
        workDaysJs={workDaysJs}
        targetDayMinutes={prefs.targetMinutesPerDay}
        absenceOvershoot={dayOvershoot}
      />,
    );

    const wd = date.getDay(); // 0 = Sun … 6 = Sat
    const isWorkingDay = workDaysJs.has(wd);
    if (isWorkingDay) weekWorkingDays += 1;
    weekWork += d.workMinutes;
    weekAbsence += d.absenceMinutes;
    if (dayOvershoot) weekAbsenceOvershoot = true;
  }
  // Flush the final week.
  if (currentWeek !== -1) emitWeekTotal("final");
  return out;
}

function DayRow({
  day,
  date,
  workDaysJs,
  targetDayMinutes,
  absenceOvershoot,
}: {
  day: MonthDayRollup;
  date: Date;
  workDaysJs: Set<number>;
  targetDayMinutes: number;
  absenceOvershoot: boolean;
}) {
  const wd = date.getDay();
  const isWorkingDay = workDaysJs.has(wd);
  const isWeekend = wd === 0 || wd === 6;
  // "Filled" iff this day has any work or any absence registered.
  const filled = day.workMinutes > 0 || day.absenceMinutes > 0;

  // Target comparison only applies on working days. Absence covers the
  // target (a Verlof-dag isn't an under-fill); we treat work + absence
  // against the day target so a full day off shows on-target rather than
  // 8h short.
  const covered = day.workMinutes + day.absenceMinutes;
  const status: TargetStatus = !isWorkingDay
    ? "weekend"
    : !filled
      ? "missing"
      : covered < targetDayMinutes
        ? "under"
        : covered === targetDayMinutes
          ? "on"
          : "over";

  return (
    <tr
      className={cn(
        "border-b border-glass last:border-b-0",
        status === "missing" && "bg-red-500/[0.04]",
        isWeekend && "text-muted-foreground/70",
      )}
    >
      <td className="px-3 py-2 font-mono text-xs">{day.date}</td>
      <td className="px-3 py-2 text-xs">
        {WEEKDAY_LABEL[wd]}
        {!isWorkingDay && (
          <span className="ml-1 text-[10px] text-muted-foreground/60">·</span>
        )}
      </td>
      <td
        className="px-3 py-2 font-mono text-xs text-foreground/80"
        title={
          day.firstLoginKind === "microsoft"
            ? "First login of the day (Microsoft 365)"
            : day.firstLoginKind === "password"
              ? "First login of the day (standard)"
              : undefined
        }
      >
        {day.firstLoginMinutes !== null ? (
          <span className="inline-flex items-center gap-1">
            {formatHHMM(day.firstLoginMinutes)}
            {day.firstLoginKind === "microsoft" && (
              <span className="text-[9px] font-sans uppercase tracking-wide text-muted-foreground/70">
                M365
              </span>
            )}
          </span>
        ) : (
          <span className="text-muted-foreground/50">—</span>
        )}
      </td>
      <td className="px-3 py-2 font-mono text-xs text-foreground/80">
        {day.firstClockMinutes !== null ? (
          formatHHMM(day.firstClockMinutes)
        ) : (
          <span className="text-muted-foreground/50">—</span>
        )}
      </td>
      <td className="px-3 py-2 font-mono text-xs text-foreground/80">
        {day.lastClockMinutes !== null ? (
          formatHHMM(day.lastClockMinutes)
        ) : (
          <span className="text-muted-foreground/50">—</span>
        )}
      </td>
      <td className="px-3 py-2">
        {day.breakdown.length === 0 ? (
          <span className="text-[11px] text-muted-foreground/50">—</span>
        ) : (
          <div className="flex flex-wrap gap-1">
            {day.breakdown.map((b) => (
              <span
                key={b.taskId}
                className={cn(
                  "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] font-medium",
                  b.isAbsence
                    ? "border-amber-400/30 bg-amber-400/10 text-amber-200"
                    : "border-glass bg-glass text-foreground/80",
                )}
              >
                <span>{b.taskName}</span>
                <span className="font-mono text-muted-foreground">
                  {formatDuration(b.minutes)}
                </span>
              </span>
            ))}
          </div>
        )}
      </td>
      <td
        className={cn(
          "px-3 py-2 font-mono text-xs",
          absenceOvershoot ? "text-red-200 font-medium" : "text-amber-200/80",
        )}
        title={absenceOvershoot ? "Above the daily absence ceiling" : undefined}
      >
        {day.absenceMinutes > 0 ? formatDuration(day.absenceMinutes) : "—"}
      </td>
      <td className="px-3 py-2 font-mono text-xs text-foreground">
        {day.workMinutes > 0 ? formatDuration(day.workMinutes) : "—"}
      </td>
      <td className="px-3 py-2">
        <TargetPill status={status} />
      </td>
    </tr>
  );
}

type TargetStatus = "weekend" | "missing" | "under" | "on" | "over";

function TargetPill({ status }: { status: TargetStatus }) {
  if (status === "weekend") {
    return <span className="text-[10px] text-muted-foreground/40">—</span>;
  }
  if (status === "missing") {
    return (
      <span className="inline-flex items-center rounded-full border border-red-400/30 bg-red-400/10 px-2 py-0.5 text-[10px] font-medium text-red-300">
        Not filled
      </span>
    );
  }
  if (status === "under") {
    return (
      <span className="inline-flex items-center rounded-full border border-amber-400/30 bg-amber-400/10 px-2 py-0.5 text-[10px] font-medium text-amber-200">
        Under target
      </span>
    );
  }
  if (status === "over") {
    return (
      <span className="inline-flex items-center rounded-full border border-emerald-400/30 bg-emerald-400/10 px-2 py-0.5 text-[10px] font-medium text-emerald-200">
        Over target
      </span>
    );
  }
  return (
    <span className="inline-flex items-center rounded-full border border-emerald-400/30 bg-emerald-400/10 px-2 py-0.5 text-[10px] font-medium text-emerald-200">
      On target
    </span>
  );
}

// ---- Helpers ----------------------------------------------------------

function UserPicker({
  value,
  onChange,
  users,
}: {
  value: string;
  onChange: (id: string) => void;
  users: TimesheetUser[];
}) {
  if (users.length === 0) {
    return (
      <div className="flex h-8 min-w-[14rem] items-center rounded-md border border-glass bg-glass px-3 text-sm text-muted-foreground">
        No timesheet users
      </div>
    );
  }
  return (
    <Select value={value || undefined} onValueChange={onChange}>
      <SelectTrigger className="h-8 min-w-[14rem] text-sm">
        <SelectValue placeholder="Select agent…" />
      </SelectTrigger>
      <SelectContent>
        {users.map((u) => (
          <SelectItem key={u.id} value={u.id}>
            {u.email}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

function MonthLabel({ year, month }: { year: number; month: number }) {
  const d = new Date(year, month - 1, 1);
  const label = d.toLocaleDateString(undefined, { month: "long", year: "numeric" });
  return (
    <span className="min-w-[10rem] px-2 text-center text-sm font-medium text-foreground">
      {label}
    </span>
  );
}

function computeTotals(days: MonthDayRollup[], prefs: TimesheetPreferences) {
  const workDaysJs = isoWorkDaysToJsSet(prefs.workDays);
  let work = 0;
  let absence = 0;
  let workingDays = 0;
  for (const d of days) {
    work += d.workMinutes;
    absence += d.absenceMinutes;
    const wd = new Date(d.date + "T00:00:00").getDay();
    if (workDaysJs.has(wd)) workingDays += 1;
  }
  return { work, absence, workingDays };
}

/// Server returns ISO weekday numbers (1=Mon..7=Sun); JS Date.getDay()
/// returns (0=Sun..6=Sat). Convert once at the boundary so the rendering
/// loops can keep using getDay().
function isoWorkDaysToJsSet(iso: number[]): Set<number> {
  const out = new Set<number>();
  for (const d of iso) {
    out.add(d === 7 ? 0 : d);
  }
  return out;
}

/// ISO 8601 week number for a JS Date. Used to group day rows into weeks
/// for the subtotal row.
function isoWeekNumber(d: Date): number {
  const date = new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()));
  const dayNum = (date.getUTCDay() + 6) % 7;
  date.setUTCDate(date.getUTCDate() - dayNum + 3);
  const firstThursday = new Date(Date.UTC(date.getUTCFullYear(), 0, 4));
  const diff = date.getTime() - firstThursday.getTime();
  return 1 + Math.round(diff / (7 * 24 * 3600 * 1000));
}

const WEEKDAY_LABEL: Record<number, string> = {
  0: "Sun",
  1: "Mon",
  2: "Tue",
  3: "Wed",
  4: "Thu",
  5: "Fri",
  6: "Sat",
};

/// Last-resort fallback used only while the preferences query is in
/// flight. Real values (global defaults + per-user override) come from
/// /api/timesheet/manager/preferences/{userId} in v0.0.35-E. WorkDays
/// here are ISO numbers (1=Mon..7=Sun) to match the server contract;
/// `isoWorkDaysToJsSet` converts to JS-day for the render loop.
const FALLBACK_PREFS: TimesheetPreferences = {
  dayStartMinutes: 8 * 60 + 30,
  targetMinutesPerDay: 8 * 60,
  targetMinutesPerWeek: 40 * 60,
  workDays: [1, 2, 3, 4, 5],
  // v0.0.36 — mirror the server-side defaults from SettingDefaults so the
  // ~50ms loading window before /preferences resolves doesn't show absence-
  // overshoot flags differently from the eventual real values.
  maxAbsenceMinutesPerDay: 30,
  officeStartMinutes: 8 * 60 + 30,
  officeEndMinutes: 17 * 60,
  // v0.0.74 — Tab 3 (manager grid) doesn't use the default-task preference;
  // it's only meaningful for the owner's own Tab 1 row-seeding.
  defaultTaskId: null,
};
