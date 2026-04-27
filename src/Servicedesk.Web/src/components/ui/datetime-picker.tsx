import * as React from "react";
import { Calendar as CalendarIcon, ChevronLeft, ChevronRight, X } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";

/**
 * Glass-styled date+time picker. Operates in *server-local* time: the admin
 * sees and edits the same wall clock the rest of the app shows, regardless
 * of their own browser timezone. The component round-trips through UTC ISO
 * strings — the offset is provided by the caller (typically from
 * useServerTime().time.offsetMinutes).
 *
 * value=null means "no datetime set".
 */
export type DateTimePickerProps = {
  value: string | null;
  offsetMinutes: number;
  onChange: (iso: string | null) => void;
  disabled?: boolean;
  placeholder?: string;
  className?: string;
  /** Optional minimum (UTC ISO) — earlier picks are disabled. */
  minUtc?: string | null;
};

type LocalParts = { y: number; m: number; d: number; hh: number; mm: number };

function utcIsoToLocalParts(iso: string | null, offsetMinutes: number): LocalParts | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return null;
  const local = new Date(t + offsetMinutes * 60_000);
  return {
    y: local.getUTCFullYear(),
    m: local.getUTCMonth(),
    d: local.getUTCDate(),
    hh: local.getUTCHours(),
    mm: local.getUTCMinutes(),
  };
}

function localPartsToUtcIso(p: LocalParts, offsetMinutes: number): string {
  const localMs = Date.UTC(p.y, p.m, p.d, p.hh, p.mm, 0, 0);
  return new Date(localMs - offsetMinutes * 60_000).toISOString();
}

function pad2(n: number): string {
  return String(n).padStart(2, "0");
}

function formatDisplay(parts: LocalParts | null): string {
  if (!parts) return "";
  return `${parts.y}-${pad2(parts.m + 1)}-${pad2(parts.d)} ${pad2(parts.hh)}:${pad2(parts.mm)}`;
}

const MONTH_NAMES = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December",
];
const WEEKDAY_LABELS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

function buildMonthGrid(year: number, monthIdx: number): { d: number; m: number; y: number; outside: boolean }[] {
  // Monday-first 6×7 grid.
  const first = new Date(Date.UTC(year, monthIdx, 1));
  const firstDow = (first.getUTCDay() + 6) % 7; // shift Sun=0 → 6, Mon=1 → 0
  const cells: { d: number; m: number; y: number; outside: boolean }[] = [];
  // 42 cells starting from (1 - firstDow) of this month.
  for (let i = 0; i < 42; i++) {
    const dt = new Date(Date.UTC(year, monthIdx, 1 - firstDow + i));
    cells.push({
      y: dt.getUTCFullYear(),
      m: dt.getUTCMonth(),
      d: dt.getUTCDate(),
      outside: dt.getUTCFullYear() !== year || dt.getUTCMonth() !== monthIdx,
    });
  }
  return cells;
}

export function DateTimePicker({
  value,
  offsetMinutes,
  onChange,
  disabled,
  placeholder = "Select date & time",
  className,
  minUtc,
}: DateTimePickerProps) {
  const [open, setOpen] = React.useState(false);

  const valueParts = utcIsoToLocalParts(value, offsetMinutes);
  const minParts = utcIsoToLocalParts(minUtc ?? null, offsetMinutes);

  // The view tracks which month is shown in the calendar — anchored to the
  // selected value when present, else to "now in server-local".
  const today = React.useMemo(
    () => utcIsoToLocalParts(new Date().toISOString(), offsetMinutes)!,
    [offsetMinutes],
  );
  const [view, setView] = React.useState({
    y: valueParts?.y ?? today.y,
    m: valueParts?.m ?? today.m,
  });

  // When the popover opens, snap the visible month to the current value (or today)
  // so reopening doesn't strand the admin in a far-away month.
  React.useEffect(() => {
    if (open) {
      setView({ y: valueParts?.y ?? today.y, m: valueParts?.m ?? today.m });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const grid = React.useMemo(() => buildMonthGrid(view.y, view.m), [view.y, view.m]);

  // Default time when picking a date with no prior value: the next quarter-hour
  // from "now in server-local" — feels more natural than 00:00 for maintenance
  // windows that are typically a few hours away.
  const defaultTime = React.useMemo(() => {
    const next = (Math.ceil(today.mm / 15) % 4) * 15;
    const carry = today.mm > 45;
    return { hh: (today.hh + (carry ? 1 : 0)) % 24, mm: next };
  }, [today.hh, today.mm]);

  const commit = (next: LocalParts) => {
    onChange(localPartsToUtcIso(next, offsetMinutes));
  };

  const onPickDay = (cell: { y: number; m: number; d: number }) => {
    const base = valueParts ?? { ...defaultTime, ...cell };
    commit({ ...base, y: cell.y, m: cell.m, d: cell.d });
    setView({ y: cell.y, m: cell.m });
  };

  const onPickHour = (raw: string) => {
    const n = clamp(parseInt(raw, 10), 0, 23);
    if (Number.isNaN(n)) return;
    const base = valueParts ?? { ...today, ...defaultTime };
    commit({ ...base, hh: n });
  };
  const onPickMinute = (raw: string) => {
    const n = clamp(parseInt(raw, 10), 0, 59);
    if (Number.isNaN(n)) return;
    const base = valueParts ?? { ...today, ...defaultTime };
    commit({ ...base, mm: n });
  };

  const display = formatDisplay(valueParts);
  const hasValue = valueParts !== null;

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          disabled={disabled}
          className={cn(
            "group flex h-9 w-full items-center gap-2 rounded-md border border-white/10 bg-white/[0.03] px-3 text-left text-sm font-mono shadow-sm transition-colors",
            "hover:bg-white/[0.05] focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
            "disabled:cursor-not-allowed disabled:opacity-50",
            !hasValue && "text-muted-foreground",
            className,
          )}
        >
          <CalendarIcon className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          <span className="flex-1 truncate">{hasValue ? display : placeholder}</span>
          {hasValue && !disabled && (
            <span
              role="button"
              tabIndex={0}
              aria-label="Clear"
              onClick={(e) => {
                e.stopPropagation();
                e.preventDefault();
                onChange(null);
              }}
              onKeyDown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.stopPropagation();
                  e.preventDefault();
                  onChange(null);
                }
              }}
              className="rounded p-0.5 text-muted-foreground/60 transition-colors hover:bg-white/10 hover:text-foreground"
            >
              <X className="h-3 w-3" />
            </span>
          )}
        </button>
      </PopoverTrigger>
      <PopoverContent
        align="start"
        sideOffset={6}
        className="w-[280px] border-white/10 bg-[hsl(240_12%_8%)]/95 p-3 backdrop-blur-md"
      >
        <div className="mb-2 flex items-center justify-between">
          <button
            type="button"
            onClick={() => setView(prevMonth(view))}
            className="rounded p-1 text-muted-foreground transition-colors hover:bg-white/10 hover:text-foreground"
            aria-label="Previous month"
          >
            <ChevronLeft className="h-4 w-4" />
          </button>
          <div className="text-sm font-medium text-foreground">
            {MONTH_NAMES[view.m]} {view.y}
          </div>
          <button
            type="button"
            onClick={() => setView(nextMonth(view))}
            className="rounded p-1 text-muted-foreground transition-colors hover:bg-white/10 hover:text-foreground"
            aria-label="Next month"
          >
            <ChevronRight className="h-4 w-4" />
          </button>
        </div>

        <div className="grid grid-cols-7 gap-1 text-center">
          {WEEKDAY_LABELS.map((d) => (
            <div key={d} className="py-1 text-[10px] font-medium uppercase tracking-wider text-muted-foreground/50">
              {d}
            </div>
          ))}
          {grid.map((cell, i) => {
            const isSelected =
              valueParts &&
              cell.y === valueParts.y &&
              cell.m === valueParts.m &&
              cell.d === valueParts.d;
            const isToday =
              cell.y === today.y && cell.m === today.m && cell.d === today.d;
            const beforeMin = minParts ? compareDate(cell, minParts) < 0 : false;
            return (
              <button
                key={i}
                type="button"
                disabled={beforeMin}
                onClick={() => onPickDay(cell)}
                className={cn(
                  "flex h-8 w-full items-center justify-center rounded-md text-xs font-mono transition-colors",
                  cell.outside ? "text-muted-foreground/30" : "text-foreground/80",
                  !isSelected && !beforeMin && "hover:bg-white/[0.06]",
                  isToday && !isSelected && "ring-1 ring-inset ring-white/15",
                  isSelected &&
                    "bg-gradient-to-br from-violet-600 to-indigo-600 text-white shadow-[0_0_12px_-2px_rgba(139,92,246,0.6)]",
                  beforeMin && "cursor-not-allowed opacity-30",
                )}
              >
                {cell.d}
              </button>
            );
          })}
        </div>

        <div className="mt-3 flex items-center gap-2 border-t border-white/5 pt-3">
          <span className="text-[10px] font-medium uppercase tracking-wider text-muted-foreground/60">
            Time
          </span>
          <NumericSpinner
            value={valueParts?.hh ?? defaultTime.hh}
            min={0}
            max={23}
            onChange={(v) => onPickHour(String(v))}
          />
          <span className="text-foreground/60">:</span>
          <NumericSpinner
            value={valueParts?.mm ?? defaultTime.mm}
            min={0}
            max={59}
            step={5}
            onChange={(v) => onPickMinute(String(v))}
          />
          <button
            type="button"
            onClick={() => setOpen(false)}
            className="ml-auto rounded-md bg-gradient-to-r from-violet-600 to-indigo-600 px-3 py-1 text-xs font-medium text-white shadow-sm transition-opacity hover:opacity-90"
          >
            Done
          </button>
        </div>
      </PopoverContent>
    </Popover>
  );
}

function NumericSpinner({
  value,
  min,
  max,
  step = 1,
  onChange,
}: {
  value: number;
  min: number;
  max: number;
  step?: number;
  onChange: (n: number) => void;
}) {
  const [text, setText] = React.useState(pad2(value));
  React.useEffect(() => setText(pad2(value)), [value]);
  return (
    <input
      type="text"
      inputMode="numeric"
      value={text}
      onChange={(e) => {
        const cleaned = e.target.value.replace(/[^0-9]/g, "").slice(0, 2);
        setText(cleaned);
      }}
      onBlur={() => {
        const n = parseInt(text, 10);
        if (Number.isNaN(n)) {
          setText(pad2(value));
          return;
        }
        const clamped = clamp(n, min, max);
        setText(pad2(clamped));
        if (clamped !== value) onChange(clamped);
      }}
      onKeyDown={(e) => {
        if (e.key === "ArrowUp") {
          e.preventDefault();
          const n = clamp((parseInt(text, 10) || value) + step, min, max);
          setText(pad2(n));
          onChange(n);
        } else if (e.key === "ArrowDown") {
          e.preventDefault();
          const n = clamp((parseInt(text, 10) || value) - step, min, max);
          setText(pad2(n));
          onChange(n);
        } else if (e.key === "Enter") {
          (e.target as HTMLInputElement).blur();
        }
      }}
      className="h-7 w-10 rounded border border-white/10 bg-white/[0.04] text-center font-mono text-xs tabular-nums text-foreground focus:outline-none focus:ring-1 focus:ring-ring"
    />
  );
}

function clamp(n: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, n));
}

function prevMonth(v: { y: number; m: number }): { y: number; m: number } {
  return v.m === 0 ? { y: v.y - 1, m: 11 } : { y: v.y, m: v.m - 1 };
}
function nextMonth(v: { y: number; m: number }): { y: number; m: number } {
  return v.m === 11 ? { y: v.y + 1, m: 0 } : { y: v.y, m: v.m + 1 };
}

function compareDate(a: { y: number; m: number; d: number }, b: { y: number; m: number; d: number }): number {
  if (a.y !== b.y) return a.y - b.y;
  if (a.m !== b.m) return a.m - b.m;
  return a.d - b.d;
}
