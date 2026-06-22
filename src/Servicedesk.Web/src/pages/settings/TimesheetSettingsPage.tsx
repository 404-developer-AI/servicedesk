import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  AlertTriangle,
  Archive,
  ArchiveRestore,
  Bed,
  Boxes,
  CalendarCheck,
  ClipboardCheck,
  Clock,
  DatabaseBackup,
  Euro,
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
import {
  settingsApi,
  taxonomyApi,
  timesheetImportAdminApi,
  type SettingEntry,
  type Status,
} from "@/lib/api";
import { AdsolutWorkHoursDialog } from "./AdsolutWorkHoursDialog";
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
const KEY_HOURLY_RATE = "Timesheet.HourlyRate";
const KEY_RESOLVED_STATUSES = "Timesheet.ResolvedTabStatusIds";
const KEY_CWI_STATUSES = "Timesheet.CwiTabStatusIds";
const KEY_QFI_STATUSES = "Statistics.QfiStatusIds";
const KEY_WFQ_STATUSES = "Statistics.WfqStatusIds";
const KEY_REPLY_HEADER = "Timesheet.ReplyHeaderHtml";
const KEY_REPLY_ROW = "Timesheet.ReplyRowHtml";
const KEY_REPLY_FOOTER = "Timesheet.ReplyFooterHtml";
const KEY_TIME_ALERT_ENABLED = "Timesheet.TimeAlertEnabled";
const KEY_TIME_ALERT_THRESHOLD = "Timesheet.TimeAlertThresholdMinutes";
const KEY_TIME_ALERT_EXTRA = "Timesheet.TimeAlertDefaultExtraMinutes";
const KEY_TIME_ALERT_CONFIRM = "Timesheet.TimeAlertConfirmationText";

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
  const hourlyRateEntry = findEntry(query.data, KEY_HOURLY_RATE);
  const resolvedStatusesEntry = findEntry(query.data, KEY_RESOLVED_STATUSES);
  const cwiStatusesEntry = findEntry(query.data, KEY_CWI_STATUSES);
  const qfiStatusesEntry = findEntry(query.data, KEY_QFI_STATUSES);
  const wfqStatusesEntry = findEntry(query.data, KEY_WFQ_STATUSES);
  const replyHeaderEntry = findEntry(query.data, KEY_REPLY_HEADER);
  const replyRowEntry = findEntry(query.data, KEY_REPLY_ROW);
  const replyFooterEntry = findEntry(query.data, KEY_REPLY_FOOTER);
  const timeAlertEnabledEntry = findEntry(query.data, KEY_TIME_ALERT_ENABLED);
  const timeAlertThresholdEntry = findEntry(query.data, KEY_TIME_ALERT_THRESHOLD);
  const timeAlertExtraEntry = findEntry(query.data, KEY_TIME_ALERT_EXTRA);
  const timeAlertConfirmEntry = findEntry(query.data, KEY_TIME_ALERT_CONFIRM);

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

          <section className="glass-card p-6">
            <SectionHeader
              icon={<Euro className="h-5 w-5" />}
              title="Hourly rate"
              description="Gross hourly rate used to price registered hours. Drives the 'Bruto Price' column on the Timesheet → Adsolut tab (rate × registered hours, per receipt and broken down per task). Leave at 0 to keep that column blank."
            />
            {hourlyRateEntry ? (
              <HourlyRateField entry={hourlyRateEntry} />
            ) : (
              <MissingEntry keyName={KEY_HOURLY_RATE} />
            )}
          </section>

          <WorkHoursArticlesSection />

          <section className="glass-card p-6">
            <SectionHeader
              icon={<AlertTriangle className="h-5 w-5" />}
              title="Ticket hour-limit alert"
              description="Warn agents when too much time has been logged on a ticket. When enabled, opening a ticket whose total logged time (all agents combined) exceeds its limit shows a popup the agent must dismiss or act on by raising the ticket's limit. The threshold counts all logged time regardless of the billed flag. Off by default. These are the global defaults — individual queues can override the limit or force the alert on/off under Settings → Tickets → Queues."
            />
            <div className="space-y-1">
              {timeAlertEnabledEntry ? (
                <ToggleField
                  entry={timeAlertEnabledEntry}
                  label="Enable hour-limit alert"
                  hint="Master switch. When off, no popup is shown unless a queue is set to force it on. Queues set to 'off' stay silent even when this is on."
                />
              ) : (
                <MissingEntry keyName={KEY_TIME_ALERT_ENABLED} />
              )}
              {timeAlertThresholdEntry ? (
                <MinutesField
                  entry={timeAlertThresholdEntry}
                  label="Default limit"
                  hint="In minutes. 480 = 8h. The popup fires once a ticket's total logged time exceeds this. Per-ticket extensions add on top of this default."
                />
              ) : (
                <MissingEntry keyName={KEY_TIME_ALERT_THRESHOLD} />
              )}
              {timeAlertExtraEntry ? (
                <MinutesField
                  entry={timeAlertExtraEntry}
                  label="Default extra minutes"
                  hint="Pre-filled amount in the 'allow more time' dialog. The agent can change it before confirming. 60 = 1h."
                />
              ) : (
                <MissingEntry keyName={KEY_TIME_ALERT_EXTRA} />
              )}
              {timeAlertConfirmEntry ? (
                <TextAreaField
                  entry={timeAlertConfirmEntry}
                  label="Confirmation checkbox text"
                  hint="The mandatory tick an agent must accept before a ticket's limit can be raised. Re-checked server-side."
                />
              ) : (
                <MissingEntry keyName={KEY_TIME_ALERT_CONFIRM} />
              )}
            </div>
          </section>

          <section className="glass-card p-6">
            <SectionHeader
              icon={<ClipboardCheck className="h-5 w-5" />}
              title="Back-office tabs"
              description="Which ticket statuses feed the back-office Resolved and CWI tabs on the Timesheet page. A ticket is listed in the month it entered one of the selected statuses. The Resolved tab additionally hides tickets that already have an Adsolut sales receipt."
            />
            <div className="space-y-1">
              {resolvedStatusesEntry ? (
                <StatusSetField
                  entry={resolvedStatusesEntry}
                  label="Resolved tab statuses"
                  hint="Tickets in these statuses, without an Adsolut sales receipt, appear on the Resolved tab."
                />
              ) : (
                <MissingEntry keyName={KEY_RESOLVED_STATUSES} />
              )}
              {cwiStatusesEntry ? (
                <StatusSetField
                  entry={cwiStatusesEntry}
                  label="CWI tab statuses"
                  hint="Tickets in these statuses appear on the CWI (Closed Without Invoice) tab."
                />
              ) : (
                <MissingEntry keyName={KEY_CWI_STATUSES} />
              )}
            </div>
          </section>

          <section className="glass-card p-6">
            <SectionHeader
              icon={<ClipboardCheck className="h-5 w-5" />}
              title="Statistics status groups"
              description="Extra status groups for the Statistics 'Hours by status group' metric. Resolved and CWI reuse the back-office sets above; QFI and WFQ are defined here. A group with no statuses is left out of the chart."
            />
            <div className="space-y-1">
              {qfiStatusesEntry ? (
                <StatusSetField
                  entry={qfiStatusesEntry}
                  label="QFI statuses"
                  hint="Statuses that make up the QFI group in the Hours-by-status-group metric."
                />
              ) : (
                <MissingEntry keyName={KEY_QFI_STATUSES} />
              )}
              {wfqStatusesEntry ? (
                <StatusSetField
                  entry={wfqStatusesEntry}
                  label="WFQ statuses"
                  hint="Statuses that make up the WFQ group in the Hours-by-status-group metric."
                />
              ) : (
                <MissingEntry keyName={KEY_WFQ_STATUSES} />
              )}
            </div>
          </section>

          <TasksSection />

          <MigrationImportSection />

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

// ---- Migration import section ----------------------------------------

const IMPORT_STATUS_KEY = ["settings", "timesheet", "import", "status"] as const;

function generateToken(): string {
  const bytes = new Uint8Array(30);
  crypto.getRandomValues(bytes);
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  return Array.from(bytes, (b) => chars[b % chars.length]).join("");
}

function MigrationImportSection() {
  const qc = useQueryClient();
  const [showInput, setShowInput] = React.useState(false);
  const [draft, setDraft] = React.useState("");

  const status = useQuery({
    queryKey: IMPORT_STATUS_KEY,
    queryFn: () => timesheetImportAdminApi.status(),
  });

  const setEnabled = useMutation({
    mutationFn: (enabled: boolean) => timesheetImportAdminApi.setEnabled(enabled),
    onSuccess: (_, enabled) => {
      qc.invalidateQueries({ queryKey: IMPORT_STATUS_KEY });
      toast.success(enabled ? "Migration import enabled" : "Migration import disabled");
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Failed to update"),
  });

  const setSecret = useMutation({
    mutationFn: (value: string) => timesheetImportAdminApi.setSecret(value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: IMPORT_STATUS_KEY });
      toast.success("Import token saved");
      setDraft("");
      setShowInput(false);
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Failed to save token"),
  });

  const deleteSecret = useMutation({
    mutationFn: () => timesheetImportAdminApi.deleteSecret(),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: IMPORT_STATUS_KEY });
      toast.success("Import token cleared");
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Failed to clear token"),
  });

  const configured = status.data?.secretConfigured ?? false;
  const showDraftInput = showInput || !configured;

  return (
    <section className="glass-card p-6">
      <SectionHeader
        icon={<DatabaseBackup className="h-5 w-5" />}
        title="Migration import"
        description="Pre-shared secret and master switch for the standalone migration tool that bulk-imports historical timesheet rows. The surface is only live when the toggle is on AND a token is configured."
      />

      {status.isLoading ? (
        <div className="space-y-3">
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-10 w-full" />
        </div>
      ) : (
        <div className="space-y-0">
          <div className="flex items-start justify-between gap-4 border-b border-glass py-3">
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium text-foreground">Enable import surface</p>
              <p className="mt-0.5 text-xs text-muted-foreground">
                When off, the migration endpoint rejects all requests regardless of the token.
              </p>
            </div>
            <div className="shrink-0">
              <Switch
                checked={status.data?.enabled ?? false}
                disabled={setEnabled.isPending}
                onCheckedChange={(v) => setEnabled.mutate(v)}
              />
            </div>
          </div>

          <div className="py-3">
            <div className="mb-3 flex flex-wrap items-center gap-3">
              <p className="text-sm font-medium text-foreground">Import token</p>
              {configured && !showInput && (
                <>
                  <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                    Token configured
                  </Badge>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2 text-xs"
                    onClick={() => { setShowInput(true); setDraft(""); }}
                  >
                    Replace token
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2 text-xs text-destructive hover:text-destructive"
                    disabled={deleteSecret.isPending}
                    onClick={() => deleteSecret.mutate()}
                  >
                    Clear token
                  </Button>
                </>
              )}
            </div>

            {showDraftInput && (
              <div className="space-y-2">
                <div className="flex items-center gap-2">
                  <Input
                    type="text"
                    value={draft}
                    onChange={(e) => setDraft(e.target.value)}
                    placeholder="Paste or generate a token…"
                    className="h-9 flex-1 font-mono text-sm"
                    disabled={setSecret.isPending}
                  />
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-9 shrink-0 px-3"
                    onClick={() => setDraft(generateToken())}
                    disabled={setSecret.isPending}
                  >
                    Generate
                  </Button>
                  <Button
                    size="sm"
                    className="h-9 shrink-0 px-3"
                    disabled={draft.length < 24 || setSecret.isPending}
                    onClick={() => setSecret.mutate(draft)}
                  >
                    Save token
                  </Button>
                  {showInput && configured && (
                    <Button
                      size="sm"
                      variant="ghost"
                      className="h-9 shrink-0 px-2 text-xs text-muted-foreground"
                      onClick={() => { setShowInput(false); setDraft(""); }}
                      disabled={setSecret.isPending}
                    >
                      Cancel
                    </Button>
                  )}
                </div>
                <p className="text-xs text-muted-foreground">
                  24–256 characters. Store it safely — it is shown only once here and never displayed again.
                </p>
              </div>
            )}
          </div>
        </div>
      )}
    </section>
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

// ---- Work-hours articles (Adsolut "VK Werkuren" matching) -------------

/// Opens the scrollable manager where an admin flags which Adsolut catalogue
/// products count as billable work hours. Drives the Timesheet → Adsolut "VK
/// Werkuren" column + the registered-hours match (hardware excluded).
function WorkHoursArticlesSection() {
  const [open, setOpen] = React.useState(false);
  return (
    <section className="glass-card p-6">
      <SectionHeader
        icon={<Boxes className="h-5 w-5" />}
        title="Work-hours articles"
        description="Choose which Adsolut catalogue products count as billable work hours. The Timesheet → Adsolut 'VK Werkuren' total — and the registered-hours match — sums only the receipt lines whose product is flagged here, so hardware no longer skews the comparison. The catalogue is mirrored alongside the sales receipts."
      />
      <FieldRow
        label="Manage work-hours articles"
        hint="Search, filter and tick the products that represent work hours. Changes apply to the matching immediately."
      >
        <Button size="sm" variant="ghost" className="h-9 gap-1.5" onClick={() => setOpen(true)}>
          <Boxes className="h-4 w-4" />
          Manage articles
        </Button>
      </FieldRow>
      <AdsolutWorkHoursDialog open={open} onClose={() => setOpen(false)} />
    </section>
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

// ---- Boolean toggle field (saves "true"/"false") ----------------------

function ToggleField({
  entry,
  label,
  hint,
}: {
  entry: SettingEntry;
  label: string;
  hint: string;
}) {
  const qc = useQueryClient();
  const checked = entry.value === "true";

  const save = useMutation({
    mutationFn: (val: boolean) => settingsApi.update(entry.key, String(val)),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success(`${label} updated`);
    },
    onError: (err) =>
      toast.error(err instanceof Error ? err.message : "Failed to save"),
  });

  return (
    <FieldRow label={label} hint={hint}>
      <Switch
        checked={checked}
        disabled={save.isPending}
        onCheckedChange={(v) => save.mutate(v)}
      />
    </FieldRow>
  );
}

// ---- Multi-line text field (full-width) -------------------------------

function TextAreaField({
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

  const commit = () => {
    const trimmed = draft.trim();
    if (trimmed === entry.value) return;
    if (trimmed.length === 0) {
      toast.error("This text cannot be empty");
      setDraft(entry.value);
      return;
    }
    save.mutate(trimmed);
  };

  return (
    <div className="border-b border-glass py-3 last:border-b-0">
      <p className="text-sm font-medium text-foreground">{label}</p>
      <p className="mt-0.5 mb-2 text-xs text-muted-foreground">{hint}</p>
      <textarea
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        rows={2}
        className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-60"
        disabled={save.isPending}
      />
    </div>
  );
}

// ---- Hourly-rate (money) field ----------------------------------------

function HourlyRateField({ entry }: { entry: SettingEntry }) {
  const qc = useQueryClient();
  const [draft, setDraft] = React.useState(entry.value);
  React.useEffect(() => setDraft(entry.value), [entry.value]);

  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success("Hourly rate updated");
    },
    onError: (err) => {
      toast.error(err instanceof Error ? err.message : "Failed to save");
      setDraft(entry.value);
    },
  });

  // Accept "75", "75.50" or the Belgian "75,50"; normalise to a dot for storage.
  const parseRate = (raw: string): number | null => {
    const n = Number.parseFloat(raw.trim().replace(",", "."));
    return Number.isFinite(n) && n >= 0 ? n : null;
  };

  const commit = () => {
    if (draft === entry.value) return;
    const parsed = parseRate(draft);
    if (parsed === null) {
      toast.error("Enter a non-negative amount, e.g. 75 or 75.50");
      setDraft(entry.value);
      return;
    }
    save.mutate(String(parsed));
  };

  return (
    <FieldRow
      label="Gross hourly rate (EUR)"
      hint="Used for the Bruto Price column. Enter a number like 75 or 75.50 (comma also accepted). 0 = column stays blank."
    >
      <div className="flex items-center gap-2">
        <span className="text-sm text-muted-foreground">€</span>
        <Input
          inputMode="decimal"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === "Enter") (e.target as HTMLInputElement).blur();
          }}
          className="h-9 w-32 font-mono"
          placeholder="0"
          disabled={save.isPending}
        />
      </div>
    </FieldRow>
  );
}

// ---- Status multi-select for the back-office tabs ---------------------

/// Stores a CSV of status ids. Statuses are toggled by name; the stored
/// value is the id so a rename keeps the selection. Several statuses can
/// share a state-category, so the picker lists every active status (the
/// category is shown only as a muted hint).
function StatusSetField({
  entry,
  label,
  hint,
}: {
  entry: SettingEntry;
  label: string;
  hint: string;
}) {
  const qc = useQueryClient();
  const statusesQuery = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
    staleTime: 60_000,
  });
  const statuses = (statusesQuery.data ?? []).filter((s) => s.isActive);

  const [draft, setDraft] = React.useState<Set<string>>(() => parseIdCsv(entry.value));
  React.useEffect(() => setDraft(parseIdCsv(entry.value)), [entry.value]);

  const save = useMutation({
    mutationFn: (val: string) => settingsApi.update(entry.key, val),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      toast.success(`${label} updated`);
    },
    onError: (err) => toast.error(err instanceof Error ? err.message : "Failed to save"),
  });

  const dirty = idCsvFromSet(draft) !== normaliseIdCsv(entry.value);

  const toggle = (id: string) => {
    const next = new Set(draft);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setDraft(next);
  };

  return (
    <FieldRow label={label} hint={hint}>
      <div className="flex max-w-md flex-col items-end gap-2">
        {statusesQuery.isLoading ? (
          <Skeleton className="h-8 w-64" />
        ) : statuses.length === 0 ? (
          <p className="text-xs text-muted-foreground">No statuses defined.</p>
        ) : (
          <div className="flex flex-wrap justify-end gap-1.5">
            {statuses.map((s) => (
              <StatusChip key={s.id} status={s} on={draft.has(s.id)} onToggle={() => toggle(s.id)} disabled={save.isPending} />
            ))}
          </div>
        )}
        <div className="flex items-center gap-2">
          <Button
            size="sm"
            variant="ghost"
            disabled={!dirty || save.isPending}
            onClick={() => save.mutate(idCsvFromSet(draft))}
            className="h-8 px-3"
          >
            Save
          </Button>
          {dirty && (
            <Button
              size="sm"
              variant="ghost"
              disabled={save.isPending}
              onClick={() => setDraft(parseIdCsv(entry.value))}
              className="h-8 px-2 text-xs text-muted-foreground"
            >
              Reset
            </Button>
          )}
        </div>
      </div>
    </FieldRow>
  );
}

function StatusChip({
  status,
  on,
  onToggle,
  disabled,
}: {
  status: Status;
  on: boolean;
  onToggle: () => void;
  disabled: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onToggle}
      disabled={disabled}
      aria-pressed={on}
      title={status.stateCategory}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-medium transition-colors",
        "disabled:cursor-not-allowed disabled:opacity-50",
        on
          ? "border-violet-400/40 bg-violet-400/15 text-foreground"
          : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover",
      )}
    >
      <span
        className="h-2 w-2 shrink-0 rounded-full"
        style={{ backgroundColor: status.color }}
        aria-hidden
      />
      {status.name}
      <span className="text-[9px] uppercase tracking-wider text-muted-foreground/50">
        {status.stateCategory}
      </span>
    </button>
  );
}

function parseIdCsv(csv: string): Set<string> {
  const out = new Set<string>();
  for (const part of csv.split(",").map((s) => s.trim())) {
    if (part) out.add(part);
  }
  return out;
}

function idCsvFromSet(set: Set<string>): string {
  return [...set].sort().join(",");
}

function normaliseIdCsv(csv: string): string {
  return idCsvFromSet(parseIdCsv(csv));
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
