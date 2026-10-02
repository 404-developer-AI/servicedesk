import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import {
  ArrowRight,
  Check,
  ChevronUp,
  Clock,
  History,
  Info,
  ListTodo,
  NotebookPen,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";
import {
  timesheetEntryApi,
  timesheetTaskApi,
  timesheetPreferencesApi,
  todayLocalIso,
  currentLocalMinutes,
  parseHHMM,
  formatHHMM,
  formatDuration,
  autoFormatTimeInput,
  seedTaskId,
  nextStartMinutes,
  validateEntryDraft,
  parseTimesheetFieldErrors,
  DEFAULT_DAY_START_MINUTES,
  type TimesheetEntry,
} from "@/lib/timesheet-api";

/// v0.1.19 — inline "Log time" form on the ticket's Time-logged panel.
/// Creates a normal timesheet entry for the signed-in agent on *this*
/// ticket, with exactly the Timesheet page's rules: start = latest end of
/// the agent's own entries today (else their day start), end = now (kept
/// current until edited), task defaults to their preferred task, a
/// description is required. The server re-validates everything.
export function TicketLogTimeForm({
  ticketId,
  ticketNumber,
  ticketSubject,
  onClose,
}: {
  ticketId: string;
  ticketNumber: number;
  ticketSubject: string;
  onClose: () => void;
}) {
  const qc = useQueryClient();
  // Same "today" as the Timesheet page (the agent's local day), so the new
  // row lands on the day they see there.
  const [entryDate] = React.useState(todayLocalIso);

  const entriesQ = useQuery({
    queryKey: ["timesheet", "entries", entryDate],
    queryFn: () => timesheetEntryApi.listByDate(entryDate),
    staleTime: 15_000,
  });
  const tasksQ = useQuery({
    queryKey: ["timesheet", "tasks"],
    queryFn: () => timesheetTaskApi.list(false),
    staleTime: 60_000,
  });
  const prefsQ = useQuery({
    queryKey: ["timesheet", "me", "preferences"],
    queryFn: () => timesheetPreferencesApi.me(),
    staleTime: 60_000,
  });

  // Absence tasks (holiday, sick…) never belong on a ticket.
  const tasks = React.useMemo(
    () => (tasksQ.data ?? []).filter((t) => !t.archived && !t.isAbsence),
    [tasksQ.data],
  );
  const myEntries = React.useMemo(() => entriesQ.data?.items ?? [], [entriesQ.data]);
  const lastEntry: TimesheetEntry | null = React.useMemo(
    () =>
      myEntries.length === 0
        ? null
        : myEntries.reduce((a, b) => (b.endMinutes > a.endMinutes ? b : a)),
    [myEntries],
  );

  const ready = entriesQ.isSuccess && tasksQ.isSuccess && prefsQ.isSuccess;

  const [startText, setStartText] = React.useState("");
  const [endText, setEndText] = React.useState("");
  const [taskId, setTaskId] = React.useState<string>("");
  const [description, setDescription] = React.useState("");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const seeded = React.useRef(false);
  // While End still shows the value we filled in, keep it at "now".
  const [endIsAuto, setEndIsAuto] = React.useState(true);
  const descRef = React.useRef<HTMLInputElement>(null);

  React.useEffect(() => {
    if (!ready || seeded.current) return;
    seeded.current = true;
    const dayStart = prefsQ.data?.dayStartMinutes ?? DEFAULT_DAY_START_MINUTES;
    setStartText(formatHHMM(nextStartMinutes(myEntries, dayStart)));
    setEndText(formatHHMM(currentLocalMinutes()));
    const seed = seedTaskId(tasks, prefsQ.data?.defaultTaskId);
    setTaskId(seed ?? "");
    window.setTimeout(() => descRef.current?.focus(), 0);
  }, [ready, myEntries, tasks, prefsQ.data]);

  React.useEffect(() => {
    if (!endIsAuto) return;
    const id = window.setInterval(() => setEndText(formatHHMM(currentLocalMinutes())), 15_000);
    return () => window.clearInterval(id);
  }, [endIsAuto]);

  const startMinutes = parseHHMM(startText);
  const endMinutes = parseHHMM(endText);
  const totalMinutes =
    startMinutes !== null && endMinutes !== null && endMinutes > startMinutes
      ? endMinutes - startMinutes
      : null;
  const startIsAuto = lastEntry !== null && startMinutes === lastEntry.endMinutes;

  const save = useMutation({
    mutationFn: () =>
      timesheetEntryApi.create({
        entryDate,
        startMinutes: startMinutes!,
        endMinutes: endMinutes!,
        taskId,
        ticketId,
        description: description.trim(),
      }),
    onSuccess: (saved) => {
      qc.invalidateQueries({ queryKey: ["timesheet", "entries", entryDate] });
      qc.invalidateQueries({ queryKey: ["timesheet", "ticket", ticketId] });
      qc.invalidateQueries({ queryKey: ["timesheet", "ticket-alert", ticketId] });
      toast.success(`Logged ${formatDuration(saved.minutes)} on #${ticketNumber}`);
      onClose();
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 422) {
        const parsed = parseTimesheetFieldErrors(err.message);
        if (parsed) {
          setErrors(parsed);
          return;
        }
      }
      toast.error(err instanceof ApiError ? err.message : "Could not log the time.");
    },
  });

  function submit() {
    if (save.isPending) return;
    const fieldErrors = validateEntryDraft({
      startMinutes,
      endMinutes,
      taskId,
      hasTicket: true,
      requiresTicket: false,
      description,
    });
    setErrors(fieldErrors);
    if (Object.keys(fieldErrors).length > 0) return;
    save.mutate();
  }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Escape") {
      e.preventDefault();
      onClose();
    } else if (e.key === "Enter" && !(e.target instanceof HTMLSelectElement)) {
      e.preventDefault();
      submit();
    }
  };

  return (
    <div className="sd-log-time border-t border-glass bg-primary/[0.03] px-4 pb-4 pt-3" onKeyDown={onKeyDown}>
      {/* Title */}
      <div className="flex items-center justify-between gap-3">
        <h3 className="min-w-0 truncate text-[15px] font-semibold text-foreground">
          Log time on <span className="text-primary">#{ticketNumber}</span>
          <span className="ml-2 font-normal text-foreground/80">{ticketSubject}</span>
        </h3>
        <button
          type="button"
          onClick={onClose}
          className="flex h-6 w-6 shrink-0 items-center justify-center rounded text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
          aria-label="Close"
          title="Close (Esc)"
        >
          <ChevronUp className="h-4 w-4" />
        </button>
      </div>

      {/* Last entry in the agent's own timesheet today */}
      <div className="mt-3 overflow-hidden rounded-lg border border-primary/25">
        <div className="flex items-center justify-between gap-3 bg-primary/10 px-3 py-1.5">
          <span className="inline-flex items-center gap-2 text-xs font-medium text-foreground/90">
            <NotebookPen className="h-3.5 w-3.5 text-primary" />
            Last logged in your timesheet
          </span>
          <Link
            to="/timesheet"
            className="inline-flex items-center gap-1 text-xs font-medium text-primary hover:underline"
          >
            Open timesheet
            <ArrowRight className="h-3 w-3" />
          </Link>
        </div>
        <div className="flex min-w-0 items-center gap-5 px-3 py-2 text-xs text-foreground/85">
          {!entriesQ.isSuccess ? (
            <span className="text-muted-foreground">Loading…</span>
          ) : lastEntry ? (
            <>
              <History className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
              <span className="shrink-0 tabular-nums">
                {formatHHMM(lastEntry.startMinutes)} – {formatHHMM(lastEntry.endMinutes)}
              </span>
              <span className="shrink-0 tabular-nums">{formatDuration(lastEntry.minutes)}</span>
              <span className="shrink-0">{lastEntry.taskName}</span>
              <span className="min-w-0 truncate text-muted-foreground" title={lastEntry.description}>
                {lastEntry.description}
              </span>
            </>
          ) : (
            <span className="text-muted-foreground">
              Nothing logged today yet — the start time is your day start.
            </span>
          )}
        </div>
      </div>

      {/* Start / End / Total + hint */}
      <div className="mt-3 flex flex-wrap items-start gap-x-4 gap-y-3">
        <TimeField
          label="Start"
          value={startText}
          onChange={setStartText}
          hint={startIsAuto ? "Auto-filled (end of previous entry)" : lastEntry ? "Edited" : "Your day start"}
          error={errors.startMinutes}
        />
        <TimeField
          label="End"
          value={endText}
          onChange={(v) => {
            setEndIsAuto(false);
            setEndText(v);
          }}
          hint={endIsAuto ? "Now (editable)" : "Edited"}
          error={errors.endMinutes}
        />
        <div className="w-28 space-y-1">
          <span className="text-xs font-medium text-foreground">Total</span>
          <div className="flex h-9 items-center rounded-md bg-glass-strong px-3 text-sm font-medium tabular-nums text-foreground">
            {totalMinutes !== null ? formatDuration(totalMinutes) : "—"}
          </div>
        </div>
        <div className="ml-auto flex max-w-[15rem] items-start gap-2 rounded-lg border border-sky-400/30 bg-sky-400/10 px-3 py-2 text-xs leading-snug text-foreground/80">
          <Info className="mt-0.5 h-3.5 w-3.5 shrink-0 text-sky-500" />
          <span>
            The start time is filled in automatically from your <strong>latest timesheet entry</strong>.
          </span>
        </div>
      </div>

      {/* Task + description */}
      <div className="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-[minmax(0,15rem)_minmax(0,1fr)]">
        <label className="space-y-1">
          <span className="text-xs font-medium text-foreground">Task</span>
          <div className="relative">
            <ListTodo className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <select
              value={taskId}
              onChange={(e) => setTaskId(e.target.value)}
              disabled={!tasksQ.isSuccess}
              className={cn(
                "h-9 w-full rounded-md border bg-glass pl-9 pr-3 text-sm text-foreground",
                "focus:outline-none focus:ring-1 focus:ring-ring",
                "[&_option]:bg-popover [&_option]:text-popover-foreground",
                errors.taskId ? "border-destructive" : "border-glass",
              )}
            >
              {tasks.length === 0 && <option value="">No active tasks</option>}
              {tasks.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          </div>
          {errors.taskId && <span className="text-[11px] text-destructive">{errors.taskId}</span>}
        </label>
        <label className="space-y-1">
          <span className="text-xs font-medium text-foreground">Description</span>
          <input
            ref={descRef}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            maxLength={2000}
            placeholder="What did you do?"
            className={cn(
              "h-9 w-full rounded-md border bg-glass px-3 text-sm text-foreground placeholder:text-muted-foreground",
              "focus:outline-none focus:ring-1 focus:ring-ring",
              errors.description ? "border-destructive" : "border-glass",
            )}
          />
          {errors.description && (
            <span className="text-[11px] text-destructive">{errors.description}</span>
          )}
        </label>
      </div>
      {errors.ticketId && <p className="mt-2 text-[11px] text-destructive">{errors.ticketId}</p>}

      <div className="mt-3 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="button" onClick={submit} disabled={!ready || save.isPending} className="gap-1.5">
          <Check className="h-4 w-4" />
          {save.isPending ? "Logging…" : "Log time"}
        </Button>
      </div>
    </div>
  );
}

function TimeField({
  label,
  value,
  onChange,
  hint,
  error,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  hint: string;
  error?: string;
}) {
  return (
    <label className="w-36 space-y-1">
      <span className="text-xs font-medium text-foreground">{label}</span>
      <div className="relative">
        <Clock className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
        <input
          value={value}
          inputMode="numeric"
          maxLength={5}
          placeholder="HH:MM"
          onChange={(e) => onChange(autoFormatTimeInput(e.target.value))}
          onBlur={() => {
            const m = parseHHMM(value);
            if (m !== null) onChange(formatHHMM(m));
          }}
          className={cn(
            "h-9 w-full rounded-md border bg-glass pl-9 pr-3 text-sm tabular-nums text-foreground",
            "focus:outline-none focus:ring-1 focus:ring-ring",
            error ? "border-destructive" : "border-glass",
          )}
        />
      </div>
      <span className={cn("block text-[11px] italic", error ? "not-italic text-destructive" : "text-muted-foreground")}>
        {error ?? hint}
      </span>
    </label>
  );
}
