import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, CalendarCheck, ClipboardCheck, Clock, Euro, FileCode, Target } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { settingsApi } from "@/lib/settings-api";
import { findEntry } from "@/components/settings/settingEntries";
import { MigrationImportSection } from "./timesheet/MigrationImportSection";
import { HtmlTemplateField } from "./timesheet/HtmlTemplateField";
import { WorkHoursArticlesSection } from "./timesheet/WorkHoursArticlesSection";
import { SectionHeader, MissingEntry } from "./settingsShared";
import { QUERY_KEY } from "./timesheet/timesheetSettingsKeys";
import { TimeOfDayField, MinutesField, ToggleField, TextAreaField, HourlyRateField } from "./timesheet/FieldEditors";
import { WorkDaysField } from "./timesheet/WorkDaysField";
import { TasksSection } from "./timesheet/TasksSection";
import { StatusSetField } from "./timesheet/StatusSetField";

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
const KEY_TIME_ALERT_DISABLE_PROMPT = "Timesheet.TimeAlertDisableReasonPrompt";
const KEY_TIME_ALERT_ALLOW_CANCEL = "Timesheet.TimeAlertAllowCancel";

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
  const timeAlertDisablePromptEntry = findEntry(query.data, KEY_TIME_ALERT_DISABLE_PROMPT);
  const timeAlertAllowCancelEntry = findEntry(query.data, KEY_TIME_ALERT_ALLOW_CANCEL);

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
              {timeAlertAllowCancelEntry ? (
                <ToggleField
                  entry={timeAlertAllowCancelEntry}
                  label="Allow Cancel"
                  hint="When off, the popup has no Cancel button and Esc / clicking outside no longer close it — agents must allow more time or disable hour tracking. Admins can still hold Shift to cancel without logging."
                />
              ) : (
                <MissingEntry keyName={KEY_TIME_ALERT_ALLOW_CANCEL} />
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
              {timeAlertDisablePromptEntry ? (
                <TextAreaField
                  entry={timeAlertDisablePromptEntry}
                  label="Disable-tracking reason prompt"
                  hint="Prompt above the mandatory reason field when an agent disables hour tracking for a ticket. The reason is required and posted as an internal note."
                />
              ) : (
                <MissingEntry keyName={KEY_TIME_ALERT_DISABLE_PROMPT} />
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
