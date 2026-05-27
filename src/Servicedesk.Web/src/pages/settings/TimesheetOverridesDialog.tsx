import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Clock } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { adminUserApi, type UserAdminRow } from "@/lib/ticket-api";
import {
  parseHHMM,
  formatHHMM,
  autoFormatTimeInput,
} from "@/lib/timesheet-api";
import { cn } from "@/lib/utils";

/// Per-user Timesheet preference dialog. Each field is independently
/// overridable: leave blank → fall back to the global default; type a
/// value → override only that one knob. The "Reset" buttons clear an
/// override row to NULL on the server.
export function TimesheetOverridesDialog({
  target,
  onOpenChange,
}: {
  target: UserAdminRow | null;
  onOpenChange: (open: boolean) => void;
}) {
  const open = target !== null;
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Clock className="h-4 w-4 text-primary" />
            Timesheet overrides
          </DialogTitle>
          <DialogDescription>
            Per-user values that take precedence over the global defaults from{" "}
            <span className="text-foreground/80">Settings → Timesheet</span>.
            Leave a field empty to use the global default for that one knob.
          </DialogDescription>
        </DialogHeader>
        {target && <Body target={target} onClose={() => onOpenChange(false)} />}
      </DialogContent>
    </Dialog>
  );
}

const WEEKDAYS: { iso: number; label: string }[] = [
  { iso: 1, label: "Mon" },
  { iso: 2, label: "Tue" },
  { iso: 3, label: "Wed" },
  { iso: 4, label: "Thu" },
  { iso: 5, label: "Fri" },
  { iso: 6, label: "Sat" },
  { iso: 7, label: "Sun" },
];

function Body({ target, onClose }: { target: UserAdminRow; onClose: () => void }) {
  const qc = useQueryClient();
  const queryKey = ["admin", "user", target.id, "timesheet-preferences"] as const;
  const query = useQuery({
    queryKey,
    queryFn: () => adminUserApi.getTimesheetPreferences(target.id),
  });

  // Local form state, seeded from the server response. We keep empty
  // strings (rather than nulls) for blank text inputs so the input is
  // controlled and clearing it shows the placeholder = fallback summary.
  const [dayStartText, setDayStartText] = React.useState("");
  const [dayTargetText, setDayTargetText] = React.useState("");
  const [weekTargetText, setWeekTargetText] = React.useState("");
  const [maxAbsenceText, setMaxAbsenceText] = React.useState("");
  const [officeStartText, setOfficeStartText] = React.useState("");
  const [officeEndText, setOfficeEndText] = React.useState("");
  // For work-days: null means "no override" (fall back to global), a Set
  // means an active override (even an empty set says "no work days").
  const [workDays, setWorkDays] = React.useState<Set<number> | null>(null);

  // Reseed once the query lands.
  React.useEffect(() => {
    if (!query.data) return;
    const o = query.data.override;
    setDayStartText(o.dayStartMinutes !== null ? formatHHMM(o.dayStartMinutes) : "");
    setDayTargetText(o.targetMinutesPerDay !== null ? String(o.targetMinutesPerDay) : "");
    setWeekTargetText(o.targetMinutesPerWeek !== null ? String(o.targetMinutesPerWeek) : "");
    setMaxAbsenceText(o.maxAbsenceMinutesPerDay !== null ? String(o.maxAbsenceMinutesPerDay) : "");
    setOfficeStartText(o.officeStartMinutes !== null ? formatHHMM(o.officeStartMinutes) : "");
    setOfficeEndText(o.officeEndMinutes !== null ? formatHHMM(o.officeEndMinutes) : "");
    setWorkDays(o.workDays === null ? null : new Set(o.workDays));
  }, [query.data]);

  const save = useMutation({
    mutationFn: async () => {
      // Validate + coerce locally. Empty fields = null (fallback).
      const trimmedStart = dayStartText.trim();
      const trimmedDay = dayTargetText.trim();
      const trimmedWeek = weekTargetText.trim();

      let dayStartMinutes: number | null = null;
      if (trimmedStart !== "") {
        const parsed = parseHHMM(trimmedStart);
        if (parsed === null) throw new Error("Day start must be HH:MM (e.g. 08:30).");
        dayStartMinutes = parsed;
      }
      let targetMinutesPerDay: number | null = null;
      if (trimmedDay !== "") {
        const n = Number.parseInt(trimmedDay, 10);
        if (!Number.isFinite(n) || n < 0 || n > 1440)
          throw new Error("Daily target must be 0..1440 minutes.");
        targetMinutesPerDay = n;
      }
      let targetMinutesPerWeek: number | null = null;
      if (trimmedWeek !== "") {
        const n = Number.parseInt(trimmedWeek, 10);
        if (!Number.isFinite(n) || n < 0 || n > 10080)
          throw new Error("Weekly target must be 0..10080 minutes.");
        targetMinutesPerWeek = n;
      }
      const trimmedMaxAbsence = maxAbsenceText.trim();
      let maxAbsenceMinutesPerDay: number | null = null;
      if (trimmedMaxAbsence !== "") {
        const n = Number.parseInt(trimmedMaxAbsence, 10);
        if (!Number.isFinite(n) || n < 0 || n > 1440)
          throw new Error("Max absence per day must be 0..1440 minutes.");
        maxAbsenceMinutesPerDay = n;
      }
      const trimmedOfficeStart = officeStartText.trim();
      let officeStartMinutes: number | null = null;
      if (trimmedOfficeStart !== "") {
        const parsed = parseHHMM(trimmedOfficeStart);
        if (parsed === null) throw new Error("Office start must be HH:MM (e.g. 08:30).");
        officeStartMinutes = parsed;
      }
      const trimmedOfficeEnd = officeEndText.trim();
      let officeEndMinutes: number | null = null;
      if (trimmedOfficeEnd !== "") {
        const parsed = parseHHMM(trimmedOfficeEnd);
        if (parsed === null) throw new Error("Office end must be HH:MM (e.g. 17:00).");
        officeEndMinutes = parsed;
      }
      if (
        officeStartMinutes !== null &&
        officeEndMinutes !== null &&
        officeEndMinutes <= officeStartMinutes
      ) {
        throw new Error("Office end must be after office start.");
      }
      const workDaysPayload: number[] | null = workDays === null
        ? null
        : [...workDays].sort((a, b) => a - b);

      return await adminUserApi.setTimesheetPreferences(target.id, {
        dayStartMinutes,
        targetMinutesPerDay,
        targetMinutesPerWeek,
        workDays: workDaysPayload,
        maxAbsenceMinutesPerDay,
        officeStartMinutes,
        officeEndMinutes,
      });
    },
    onSuccess: () => {
      toast.success("Timesheet overrides saved");
      qc.invalidateQueries({ queryKey });
      onClose();
    },
    onError: (err) =>
      toast.error(err instanceof Error ? err.message : "Failed to save"),
  });

  const clearAll = () => {
    setDayStartText("");
    setDayTargetText("");
    setWeekTargetText("");
    setMaxAbsenceText("");
    setOfficeStartText("");
    setOfficeEndText("");
    setWorkDays(null);
  };

  if (query.isLoading) {
    return (
      <div className="space-y-3 py-2">
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-16 w-full" />
      </div>
    );
  }
  if (!query.data) {
    return (
      <p className="py-4 text-sm text-muted-foreground">
        Could not load preferences for this user.
      </p>
    );
  }

  const effective = query.data.effective;

  return (
    <>
      <div className="space-y-2 py-1 text-xs text-muted-foreground">
        Editing for <span className="font-medium text-foreground">{target.email}</span>
      </div>

      <div className="space-y-3 py-2">
        <OverrideRow
          label="Day start"
          hint="Pre-fills the Start column on the first new row of a day on Tab 1."
          fallback={`Global default · ${formatHHMM(effective.dayStartMinutes)}`}
          onClear={() => setDayStartText("")}
          cleared={dayStartText.trim() === ""}
        >
          <Input
            value={dayStartText}
            onChange={(e) => setDayStartText(autoFormatTimeInput(e.target.value))}
            placeholder="HH:MM"
            className="h-9 w-32 font-mono"
          />
        </OverrideRow>

        <OverrideRow
          label="Target per work day"
          hint="Minutes per work-day. 480 = 8h."
          fallback={`Global default · ${effective.targetMinutesPerDay} min (${formatMinutesAsHours(effective.targetMinutesPerDay)})`}
          onClear={() => setDayTargetText("")}
          cleared={dayTargetText.trim() === ""}
        >
          <Input
            type="number"
            min={0}
            max={1440}
            value={dayTargetText}
            onChange={(e) => setDayTargetText(e.target.value)}
            placeholder="480"
            className="h-9 w-32 font-mono"
          />
        </OverrideRow>

        <OverrideRow
          label="Target per ISO week"
          hint="Minutes per week. 2400 = 40h."
          fallback={`Global default · ${effective.targetMinutesPerWeek} min (${formatMinutesAsHours(effective.targetMinutesPerWeek)})`}
          onClear={() => setWeekTargetText("")}
          cleared={weekTargetText.trim() === ""}
        >
          <Input
            type="number"
            min={0}
            max={10080}
            value={weekTargetText}
            onChange={(e) => setWeekTargetText(e.target.value)}
            placeholder="2400"
            className="h-9 w-32 font-mono"
          />
        </OverrideRow>

        <OverrideRow
          label="Max absence per day"
          hint="Minutes. If absence-task time on any day exceeds this, the entire ISO-week is flagged 'target not met' on Tab 3. 0 disables the check."
          fallback={`Global default · ${effective.maxAbsenceMinutesPerDay} min (${formatMinutesAsHours(effective.maxAbsenceMinutesPerDay)})`}
          onClear={() => setMaxAbsenceText("")}
          cleared={maxAbsenceText.trim() === ""}
        >
          <Input
            type="number"
            min={0}
            max={1440}
            value={maxAbsenceText}
            onChange={(e) => setMaxAbsenceText(e.target.value)}
            placeholder="30"
            className="h-9 w-32 font-mono"
          />
        </OverrideRow>

        <OverrideRow
          label="Office hours"
          hint="Window used by Tab 1 to flag row-to-row gaps and overlaps. A mismatch only highlights red when it falls inside this range."
          fallback={`Global default · ${formatHHMM(effective.officeStartMinutes)}–${formatHHMM(effective.officeEndMinutes)}`}
          onClear={() => {
            setOfficeStartText("");
            setOfficeEndText("");
          }}
          cleared={officeStartText.trim() === "" && officeEndText.trim() === ""}
        >
          <div className="flex items-center gap-1.5">
            <Input
              value={officeStartText}
              onChange={(e) => setOfficeStartText(autoFormatTimeInput(e.target.value))}
              placeholder="HH:MM"
              className="h-9 w-24 font-mono"
            />
            <span className="text-xs text-muted-foreground/60">–</span>
            <Input
              value={officeEndText}
              onChange={(e) => setOfficeEndText(autoFormatTimeInput(e.target.value))}
              placeholder="HH:MM"
              className="h-9 w-24 font-mono"
            />
          </div>
        </OverrideRow>

        <OverrideRow
          label="Work days"
          hint="Days counted as work-days. The 'Not filled' badge in Tab 3 ignores days outside this set."
          fallback={`Global default · ${formatWorkDays(effective.workDays)}`}
          onClear={() => setWorkDays(null)}
          cleared={workDays === null}
        >
          <div className="flex flex-wrap items-center justify-end gap-1.5">
            {WEEKDAYS.map((d) => {
              const overrideActive = workDays !== null;
              const on = overrideActive && workDays!.has(d.iso);
              return (
                <button
                  key={d.iso}
                  type="button"
                  onClick={() => {
                    const next = new Set(workDays ?? []);
                    if (next.has(d.iso)) next.delete(d.iso);
                    else next.add(d.iso);
                    setWorkDays(next);
                  }}
                  className={cn(
                    "inline-flex h-8 w-10 items-center justify-center rounded-md border text-[11px] font-medium transition-colors",
                    !overrideActive && "opacity-40",
                    on
                      ? "border-violet-400/40 bg-violet-400/15 text-violet-200 hover:bg-violet-400/20"
                      : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover",
                  )}
                  aria-pressed={on}
                >
                  {d.label}
                </button>
              );
            })}
          </div>
        </OverrideRow>
      </div>

      <DialogFooter className="mt-2 gap-2">
        <Button
          variant="ghost"
          onClick={clearAll}
          disabled={save.isPending}
          className="mr-auto text-muted-foreground"
        >
          Clear all overrides
        </Button>
        <Button variant="ghost" onClick={onClose} disabled={save.isPending}>
          Cancel
        </Button>
        <Button onClick={() => save.mutate()} disabled={save.isPending}>
          {save.isPending ? "Saving…" : "Save overrides"}
        </Button>
      </DialogFooter>
    </>
  );
}

function OverrideRow({
  label,
  hint,
  fallback,
  cleared,
  onClear,
  children,
}: {
  label: string;
  hint: string;
  fallback: string;
  cleared: boolean;
  onClear: () => void;
  children: React.ReactNode;
}) {
  return (
    <div className="flex items-start justify-between gap-3 rounded-md border border-glass-strong bg-glass px-3 py-2.5">
      <div className="min-w-0 flex-1">
        <p className="text-xs font-medium text-foreground">{label}</p>
        <p className="mt-0.5 text-[10px] text-muted-foreground">{hint}</p>
        <p className="mt-1 text-[10px] text-muted-foreground/60">
          {cleared ? fallback : "Override active"}
        </p>
      </div>
      <div className="flex flex-col items-end gap-1">
        {children}
        {!cleared && (
          <button
            type="button"
            onClick={onClear}
            className="text-[10px] text-muted-foreground hover:text-foreground"
          >
            Clear override
          </button>
        )}
      </div>
    </div>
  );
}

function formatMinutesAsHours(minutes: number): string {
  if (minutes <= 0) return "0h";
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}h`;
  return `${h}h ${m}m`;
}

function formatWorkDays(days: number[]): string {
  if (days.length === 0) return "no work days";
  return days.map((d) => WEEKDAYS.find((w) => w.iso === d)?.label ?? `?${d}`).join(", ");
}
