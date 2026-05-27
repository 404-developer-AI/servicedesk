import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  Archive,
  ArchiveRestore,
  Bed,
  CalendarCheck,
  Clock,
  FileCode,
  ListChecks,
  Pencil,
  Plus,
  Target,
  Ticket,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { settingsApi, type SettingEntry } from "@/lib/api";
import { ApiError } from "@/lib/ticket-api";
import {
  autoFormatTimeInput,
  formatHHMM,
  parseHHMM,
  timesheetTaskApi,
  type TimesheetTask,
  type TimesheetTaskUpsert,
} from "@/lib/timesheet-api";
import { cn } from "@/lib/utils";

const QUERY_KEY = ["settings", "list", "Timesheet"] as const;

const KEY_DAY_START = "Timesheet.DefaultDayStartMinutes";
const KEY_TARGET_DAY = "Timesheet.DefaultTargetMinutesPerDay";
const KEY_TARGET_WEEK = "Timesheet.DefaultTargetMinutesPerWeek";
const KEY_WORK_DAYS = "Timesheet.DefaultWorkDays";
const KEY_MAX_ABSENCE_DAY = "Timesheet.DefaultMaxAbsenceMinutesPerDay";
const KEY_OFFICE_START = "Timesheet.DefaultOfficeStartMinutes";
const KEY_OFFICE_END = "Timesheet.DefaultOfficeEndMinutes";
const KEY_REPLY_HEADER = "Timesheet.ReplyHeaderHtml";
const KEY_REPLY_ROW = "Timesheet.ReplyRowHtml";
const KEY_REPLY_FOOTER = "Timesheet.ReplyFooterHtml";

const WEEKDAYS: { iso: number; label: string }[] = [
  { iso: 1, label: "Mon" },
  { iso: 2, label: "Tue" },
  { iso: 3, label: "Wed" },
  { iso: 4, label: "Thu" },
  { iso: 5, label: "Fri" },
  { iso: 6, label: "Sat" },
  { iso: 7, label: "Sun" },
];

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

export function TimesheetSettingsPage() {
  const query = useQuery({
    queryKey: QUERY_KEY,
    queryFn: () => settingsApi.list("Timesheet"),
  });

  const startEntry = findEntry(query.data, KEY_DAY_START);
  const targetDayEntry = findEntry(query.data, KEY_TARGET_DAY);
  const targetWeekEntry = findEntry(query.data, KEY_TARGET_WEEK);
  const maxAbsenceDayEntry = findEntry(query.data, KEY_MAX_ABSENCE_DAY);
  const officeStartEntry = findEntry(query.data, KEY_OFFICE_START);
  const officeEndEntry = findEntry(query.data, KEY_OFFICE_END);
  const workDaysEntry = findEntry(query.data, KEY_WORK_DAYS);
  const replyHeaderEntry = findEntry(query.data, KEY_REPLY_HEADER);
  const replyRowEntry = findEntry(query.data, KEY_REPLY_ROW);
  const replyFooterEntry = findEntry(query.data, KEY_REPLY_FOOTER);

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <Clock className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Timesheet</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Defaults that drive the agent registration grid (Tab 1) and the
            manager month-rollup (Tab 3), plus the task catalogue agents pick
            from when registering time. Per-user overrides for the timing
            defaults live on{" "}
            <span className="text-foreground/80">Users → row action → "Timesheet overrides"</span>.
          </p>
        </div>
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      {query.isLoading && (
        <div className="space-y-3">
          <Skeleton className="h-32 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      )}

      {!query.isLoading && (
        <>
          <section className="glass-card p-6">
            <SectionHeader
              icon={<Clock className="h-5 w-5" />}
              title="Day start"
              description="Pre-fill value of the Start column for the very first new row of a day on Tab 1. Subsequent rows pre-fill from the previous row's end time."
            />
            {startEntry ? (
              <TimeOfDayField entry={startEntry} />
            ) : (
              <MissingEntry keyName={KEY_DAY_START} />
            )}
          </section>

          <section className="glass-card p-6">
            <SectionHeader
              icon={<Target className="h-5 w-5" />}
              title="Targets"
              description="Used by the colour-coding on Tab 3 (Under / On / Over). Absence-minutes count toward the daily target so a full Verlof-day is shown as on-target."
            />
            <div className="space-y-1">
              {targetDayEntry ? (
                <MinutesField
                  entry={targetDayEntry}
                  label="Target per work day"
                  hint="In minutes. 480 = 8h."
                />
              ) : (
                <MissingEntry keyName={KEY_TARGET_DAY} />
              )}
              {targetWeekEntry ? (
                <MinutesField
                  entry={targetWeekEntry}
                  label="Target per ISO week"
                  hint="In minutes. 2400 = 40h. The Tab-3 week-subtotal row compares against this."
                />
              ) : (
                <MissingEntry keyName={KEY_TARGET_WEEK} />
              )}
              {maxAbsenceDayEntry ? (
                <MinutesField
                  entry={maxAbsenceDayEntry}
                  label="Max absence per day"
                  hint="In minutes. If absence-task time on any day exceeds this ceiling, the entire ISO-week is flagged 'target not met' on Tab 3 — regardless of total time logged. 30 ≈ 2.5h per 5-day work week. 0 disables the check."
                />
              ) : (
                <MissingEntry keyName={KEY_MAX_ABSENCE_DAY} />
              )}
            </div>
          </section>

          <section className="glass-card p-6">
            <SectionHeader
              icon={<CalendarCheck className="h-5 w-5" />}
              title="Work days"
              description="Days counted as work-days for the Tab-3 'Not filled' detection. Days outside this set are shown muted and never flagged as missing."
            />
            {workDaysEntry ? (
              <WorkDaysField entry={workDaysEntry} />
            ) : (
              <MissingEntry keyName={KEY_WORK_DAYS} />
            )}
          </section>

          <section className="glass-card p-6">
            <SectionHeader
              icon={<Clock className="h-5 w-5" />}
              title="Office hours"
              description="Time window used by Tab 1 to flag row-to-row gaps and overlaps. A row whose start time doesn't connect to the previous row's end is highlighted red — but only when the mismatch falls inside this window. Outside office hours the rows stay neutral."
            />
            <div className="space-y-1">
              {officeStartEntry ? (
                <TimeOfDayField entry={officeStartEntry} label="Office start" />
              ) : (
                <MissingEntry keyName={KEY_OFFICE_START} />
              )}
              {officeEndEntry ? (
                <TimeOfDayField entry={officeEndEntry} label="Office end" />
              ) : (
                <MissingEntry keyName={KEY_OFFICE_END} />
              )}
            </div>
          </section>

          <TasksSection />

          <section className="glass-card p-6">
            <SectionHeader
              icon={<FileCode className="h-5 w-5" />}
              title="Reply template"
              description="HTML fragments used by the 'Import registered time' button on the reply editor. Three fragments are concatenated: header, row (per entry), footer. Placeholder values are HTML-escaped at render time."
            />
            <div className="space-y-1">
              {replyHeaderEntry ? (
                <HtmlTemplateField
                  entry={replyHeaderEntry}
                  label="Header"
                  hint="Emitted once before the rows. Default: a <table> opener with a header row."
                  placeholders={[]}
                />
              ) : (
                <MissingEntry keyName={KEY_REPLY_HEADER} />
              )}
              {replyRowEntry ? (
                <HtmlTemplateField
                  entry={replyRowEntry}
                  label="Row (repeated per entry)"
                  hint="Emitted once for every timesheet entry. Each placeholder is replaced with the escaped value of the corresponding column."
                  placeholders={[
                    "{{date}}",
                    "{{start}}",
                    "{{end}}",
                    "{{duration}}",
                    "{{minutes}}",
                    "{{description}}",
                    "{{agent}}",
                    "{{task}}",
                  ]}
                />
              ) : (
                <MissingEntry keyName={KEY_REPLY_ROW} />
              )}
              {replyFooterEntry ? (
                <HtmlTemplateField
                  entry={replyFooterEntry}
                  label="Footer"
                  hint="Emitted once after the rows. Useful for a totals row."
                  placeholders={[
                    "{{total_duration}}",
                    "{{total_minutes}}",
                    "{{total_hours}}",
                    "{{count}}",
                  ]}
                />
              ) : (
                <MissingEntry keyName={KEY_REPLY_FOOTER} />
              )}
            </div>
          </section>
        </>
      )}
    </div>
  );
}

// ---- HTML-template textarea field -------------------------------------

function HtmlTemplateField({
  entry,
  label,
  hint,
  placeholders,
}: {
  entry: SettingEntry;
  label: string;
  hint: string;
  placeholders: string[];
}) {
  const qc = useQueryClient();
  const [draft, setDraft] = React.useState(entry.value);
  React.useEffect(() => setDraft(entry.value), [entry.value]);

  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success(`${label} updated`);
    },
    onError: (err) => {
      toast.error(err instanceof Error ? err.message : "Failed to save");
      setDraft(entry.value);
    },
  });

  const dirty = draft !== entry.value;

  return (
    <div className="border-b border-glass py-3 last:border-b-0 space-y-2">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 flex-1">
          <p className="text-sm font-medium text-foreground">{label}</p>
          <p className="mt-0.5 text-xs text-muted-foreground">{hint}</p>
        </div>
        <div className="shrink-0 flex items-center gap-2">
          <Button
            size="sm"
            variant="ghost"
            disabled={!dirty || save.isPending}
            onClick={() => save.mutate(draft)}
            className="h-8 px-3"
          >
            Save
          </Button>
          {dirty && (
            <Button
              size="sm"
              variant="ghost"
              disabled={save.isPending}
              onClick={() => setDraft(entry.value)}
              className="h-8 px-2 text-xs text-muted-foreground"
            >
              Reset
            </Button>
          )}
        </div>
      </div>
      <textarea
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        disabled={save.isPending}
        rows={4}
        spellCheck={false}
        className={cn(
          "w-full rounded-md border border-glass bg-glass px-3 py-2",
          "font-mono text-[11px] leading-relaxed text-foreground/90",
          "focus:outline-none focus:ring-1 focus:ring-violet-400/40 focus:border-violet-400/40",
          "disabled:opacity-50",
        )}
      />
      {placeholders.length > 0 && (
        <div className="flex flex-wrap items-center gap-1">
          <span className="text-[10px] uppercase tracking-wider text-muted-foreground/70">
            Placeholders:
          </span>
          {placeholders.map((p) => (
            <code
              key={p}
              className="rounded bg-glass px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground"
            >
              {p}
            </code>
          ))}
        </div>
      )}
    </div>
  );
}

// ---- shared bits ------------------------------------------------------

function SectionHeader({
  icon,
  title,
  description,
}: {
  icon: React.ReactNode;
  title: string;
  description: string;
}) {
  return (
    <div className="mb-4 flex items-center gap-3">
      <div className="rounded-md bg-glass p-2 text-primary">{icon}</div>
      <div>
        <h2 className="text-base font-semibold text-foreground">{title}</h2>
        <p className="text-xs text-muted-foreground">{description}</p>
      </div>
    </div>
  );
}

function MissingEntry({ keyName }: { keyName: string }) {
  return (
    <p className="text-xs text-amber-200/70">
      Setting <code className="font-mono">{keyName}</code> not seeded yet —
      restart the API once to run the settings seeder.
    </p>
  );
}

// ---- HH:MM field for the day-start setting ----------------------------

function TimeOfDayField({
  entry,
  label = "Start time of a new day",
  hint = "The Start column on the first row of every new day is pre-filled with this value.",
}: {
  entry: SettingEntry;
  label?: string;
  hint?: string;
}) {
  const qc = useQueryClient();
  const initialMinutes = Number.parseInt(entry.value, 10);
  const initialText = Number.isFinite(initialMinutes) ? formatHHMM(initialMinutes) : "08:30";
  const [text, setText] = React.useState(initialText);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    setText(initialText);
  }, [initialText]);

  const save = useMutation({
    mutationFn: (minutes: number) =>
      settingsApi.update(entry.key, String(minutes)),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success(`${label} updated`);
    },
    onError: (err) =>
      toast.error(err instanceof Error ? err.message : "Failed to save"),
  });

  const commit = () => {
    const parsed = parseHHMM(text.trim());
    if (parsed === null) {
      setError("Enter a time as HH:MM (e.g. 08:30).");
      return;
    }
    setError(null);
    setText(formatHHMM(parsed));
    if (parsed !== initialMinutes) save.mutate(parsed);
  };

  return (
    <FieldRow label={label} hint={hint}>
      <Input
        value={text}
        onChange={(e) => {
          setText(autoFormatTimeInput(e.target.value));
          setError(null);
        }}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === "Enter") (e.target as HTMLInputElement).blur();
        }}
        placeholder="HH:MM"
        className={cn(
          "h-9 w-32 font-mono",
          error && "border-red-400/60",
        )}
        disabled={save.isPending}
      />
      {error && <p className="mt-1 text-[10px] text-red-300">{error}</p>}
    </FieldRow>
  );
}

// ---- Generic minutes field with "= Xh Ym" hint ------------------------

function MinutesField({
  entry,
  label,
  hint,
}: {
  entry: SettingEntry;
  label: string;
  hint: string;
}) {
  const qc = useQueryClient();
  const [draft, setDraft] = React.useState(entry.value);
  React.useEffect(() => setDraft(entry.value), [entry.value]);

  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success(`${label} updated`);
    },
    onError: (err) => {
      toast.error(err instanceof Error ? err.message : "Failed to save");
      setDraft(entry.value);
    },
  });

  const minutes = Number.parseInt(draft, 10);
  const hourSummary =
    Number.isFinite(minutes) && minutes >= 0
      ? formatMinutesAsHours(minutes)
      : "—";

  const commit = () => {
    if (draft === entry.value) return;
    const parsed = Number.parseInt(draft, 10);
    if (!Number.isFinite(parsed) || parsed < 0) {
      toast.error("Must be a non-negative whole number of minutes");
      setDraft(entry.value);
      return;
    }
    save.mutate(String(parsed));
  };

  return (
    <FieldRow label={label} hint={hint}>
      <div className="flex items-center gap-3">
        <Input
          type="number"
          min={0}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === "Enter") (e.target as HTMLInputElement).blur();
          }}
          className="h-9 w-32 font-mono"
          disabled={save.isPending}
        />
        <span className="text-xs text-muted-foreground">= {hourSummary}</span>
      </div>
    </FieldRow>
  );
}

// ---- Weekday-checkbox group for the work-days setting -----------------

function WorkDaysField({ entry }: { entry: SettingEntry }) {
  const qc = useQueryClient();
  const [draft, setDraft] = React.useState<Set<number>>(() => parseCsv(entry.value));
  React.useEffect(() => setDraft(parseCsv(entry.value)), [entry.value]);

  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success("Work days updated");
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Failed to save"),
  });

  const dirty = csvFromSet(draft) !== entry.value;

  const toggle = (iso: number) => {
    const next = new Set(draft);
    if (next.has(iso)) next.delete(iso);
    else next.add(iso);
    setDraft(next);
  };

  return (
    <FieldRow
      label="Work days"
      hint="Toggle a day to include or exclude it from the 'Not filled' detection on Tab 3."
    >
      <div className="flex flex-wrap items-center gap-2">
        {WEEKDAYS.map((d) => {
          const on = draft.has(d.iso);
          return (
            <button
              key={d.iso}
              type="button"
              onClick={() => toggle(d.iso)}
              disabled={save.isPending}
              className={cn(
                "inline-flex h-9 w-12 items-center justify-center rounded-md border text-xs font-medium transition-colors",
                "disabled:cursor-not-allowed disabled:opacity-50",
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
        <Button
          size="sm"
          variant="ghost"
          disabled={!dirty || save.isPending}
          onClick={() => save.mutate(csvFromSet(draft))}
          className="h-9 px-3"
        >
          Save
        </Button>
        {dirty && (
          <Button
            size="sm"
            variant="ghost"
            disabled={save.isPending}
            onClick={() => setDraft(parseCsv(entry.value))}
            className="h-9 px-2 text-xs text-muted-foreground"
          >
            Reset
          </Button>
        )}
      </div>
    </FieldRow>
  );
}

function parseCsv(csv: string): Set<number> {
  const out = new Set<number>();
  for (const part of csv.split(",").map((s) => s.trim())) {
    const n = Number.parseInt(part, 10);
    if (Number.isFinite(n) && n >= 1 && n <= 7) out.add(n);
  }
  return out;
}

function csvFromSet(set: Set<number>): string {
  return [...set].sort((a, b) => a - b).join(",");
}

function formatMinutesAsHours(minutes: number): string {
  if (minutes === 0) return "0h";
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}h`;
  return `${h}h ${m}m`;
}

// ---- Field-row layout helper -----------------------------------------

function FieldRow({
  label,
  hint,
  children,
}: {
  label: string;
  hint: string;
  children: React.ReactNode;
}) {
  return (
    <div className="flex items-start justify-between gap-4 border-b border-glass py-3 last:border-b-0">
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-foreground">{label}</p>
        <p className="mt-0.5 text-xs text-muted-foreground">{hint}</p>
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  );
}

// ---- Tasks catalogue (merged from the standalone tasks-settings page) -

/// Admin CRUD for the timesheet-task catalogue. Drives the Tab-1 dropdown
/// on the agent side. Archive instead of delete so historical entries
/// that still reference a retired task keep rendering correctly (FK has
/// `ON DELETE RESTRICT`).
function TasksSection() {
  const [editing, setEditing] = React.useState<TimesheetTask | "new" | null>(null);
  const [includeArchived, setIncludeArchived] = React.useState(true);

  const tasksQuery = useQuery({
    queryKey: ["timesheet", "admin", "tasks", includeArchived],
    queryFn: () => timesheetTaskApi.list(includeArchived),
  });
  const tasks = tasksQuery.data ?? [];

  return (
    <section className="glass-card p-6">
      <div className="mb-4 flex items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="rounded-md bg-glass p-2 text-primary">
            <ListChecks className="h-5 w-5" />
          </div>
          <div>
            <h2 className="text-base font-semibold text-foreground">Tasks</h2>
            <p className="text-xs text-muted-foreground">
              The catalogue agents pick from when registering time. Each task can require a
              ticket (Servicedesk, Project) or stand alone (Administratie, Verlof). Absence
              tasks roll up separately in the month overview.
            </p>
          </div>
        </div>
      </div>

      <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <Switch checked={includeArchived} onCheckedChange={setIncludeArchived} />
          Show archived
        </label>
        <Button size="sm" onClick={() => setEditing("new")}>
          <Plus className="mr-1.5 h-4 w-4" /> New task
        </Button>
      </div>

      {tasksQuery.isLoading ? (
        <div className="space-y-2">
          {[...Array(3)].map((_, i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : (
        <div className="overflow-hidden rounded-md border border-glass">
          <table className="w-full text-left text-sm">
            <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass [&_th]:bg-glass">
              <tr>
                <th className="px-4 py-3 font-medium">Name</th>
                <th className="px-4 py-3 font-medium">Requires ticket</th>
                <th className="px-4 py-3 font-medium">Absence</th>
                <th className="px-4 py-3 font-medium">Sort</th>
                <th className="px-4 py-3 font-medium">Status</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {tasks.map((t) => (
                <tr key={t.id} className="border-b border-glass hover:bg-glass-hover">
                  <td className="px-4 py-3 text-foreground">
                    <span className={cn("inline-flex items-center gap-2", t.archived && "text-muted-foreground line-through")}>
                      <Clock className="h-3.5 w-3.5 text-muted-foreground" />
                      {t.name}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-xs">
                    {t.requiresTicket ? (
                      <span className="inline-flex items-center gap-1 text-emerald-300">
                        <Ticket className="h-3 w-3" /> required
                      </span>
                    ) : (
                      <span className="text-muted-foreground">no ticket</span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-xs">
                    {t.isAbsence ? (
                      <span className="inline-flex items-center gap-1 text-amber-300">
                        <Bed className="h-3 w-3" /> absence
                      </span>
                    ) : (
                      <span className="text-muted-foreground">—</span>
                    )}
                  </td>
                  <td className="px-4 py-3 font-mono text-xs text-muted-foreground">{t.sortOrder}</td>
                  <td className="px-4 py-3">
                    {t.archived ? (
                      <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                        archived
                      </Badge>
                    ) : (
                      <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                        active
                      </Badge>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <Button size="sm" variant="ghost" onClick={() => setEditing(t)}>
                      <Pencil className="mr-1 h-3.5 w-3.5" /> Edit
                    </Button>
                  </td>
                </tr>
              ))}
              {tasks.length === 0 && (
                <tr>
                  <td colSpan={6} className="p-8 text-center text-sm text-muted-foreground">
                    No tasks defined yet. The bootstrap seeds a default set; if you see this
                    on a fresh install, restart the API.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {editing && (
        <TaskDialog
          task={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
    </section>
  );
}

function TaskDialog({ task, onClose }: { task: TimesheetTask | null; onClose: () => void }) {
  const qc = useQueryClient();
  const [form, setForm] = React.useState<TimesheetTaskUpsert>(() => ({
    name: task?.name ?? "",
    requiresTicket: task?.requiresTicket ?? true,
    isAbsence: task?.isAbsence ?? false,
    archived: task?.archived ?? false,
    sortOrder: task?.sortOrder ?? 0,
  }));
  const [error, setError] = React.useState<string | null>(null);

  const save = useMutation({
    mutationFn: async () => {
      setError(null);
      const trimmedName = form.name.trim();
      if (trimmedName.length === 0) {
        setError("Name is required.");
        throw new Error("invalid");
      }
      const body: TimesheetTaskUpsert = { ...form, name: trimmedName };
      if (task === null) return await timesheetTaskApi.create(body);
      return await timesheetTaskApi.update(task.id, body);
    },
    onSuccess: () => {
      toast.success(task === null ? "Task created" : "Task updated");
      qc.invalidateQueries({ queryKey: ["timesheet", "tasks"] });
      qc.invalidateQueries({ queryKey: ["timesheet", "admin", "tasks"] });
      onClose();
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        try {
          const parsed = JSON.parse(err.message) as { error?: string };
          if (parsed.error) {
            setError(parsed.error);
            return;
          }
        } catch {
          /* ignore */
        }
        setError(err.message);
      } else if (err instanceof Error && err.message !== "invalid") {
        setError(err.message);
      }
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{task ? "Edit task" : "New task"}</DialogTitle>
          <DialogDescription>
            Names must be unique among active tasks. Archive a task instead of deleting so
            historical entries keep their reference.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <label className="block text-xs">
            <span className="mb-1 block text-muted-foreground">Name</span>
            <Input
              value={form.name}
              autoFocus
              onChange={(e) => setForm({ ...form, name: e.target.value })}
              placeholder="e.g. Servicedesk"
            />
          </label>

          <div className="grid grid-cols-2 gap-3">
            <label className="flex items-center justify-between rounded-md border border-glass bg-glass px-3 py-2 text-xs">
              <span>
                <span className="block text-foreground">Requires ticket</span>
                <span className="text-muted-foreground">Agent must link a ticket.</span>
              </span>
              <Switch
                checked={form.requiresTicket}
                onCheckedChange={(v) => setForm({ ...form, requiresTicket: v })}
              />
            </label>
            <label className="flex items-center justify-between rounded-md border border-glass bg-glass px-3 py-2 text-xs">
              <span>
                <span className="block text-foreground">Absence</span>
                <span className="text-muted-foreground">Rolls up separately.</span>
              </span>
              <Switch
                checked={form.isAbsence}
                onCheckedChange={(v) => setForm({ ...form, isAbsence: v })}
              />
            </label>
          </div>

          <label className="block text-xs">
            <span className="mb-1 block text-muted-foreground">Sort order (lower = first)</span>
            <Input
              type="number"
              value={form.sortOrder ?? 0}
              onChange={(e) => setForm({ ...form, sortOrder: Number(e.target.value) })}
            />
          </label>

          {task !== null && (
            <label className="flex items-center justify-between rounded-md border border-glass bg-glass px-3 py-2 text-xs">
              <span className="inline-flex items-center gap-2 text-foreground">
                {form.archived ? <ArchiveRestore className="h-3.5 w-3.5" /> : <Archive className="h-3.5 w-3.5" />}
                {form.archived ? "Archived (hidden from pickers)" : "Active"}
              </span>
              <Switch
                checked={!form.archived}
                onCheckedChange={(v) => setForm({ ...form, archived: !v })}
              />
            </label>
          )}

          {error && <p className="text-sm text-red-300">{error}</p>}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button onClick={() => save.mutate()} disabled={save.isPending}>
            {save.isPending ? "Saving…" : task ? "Save" : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
