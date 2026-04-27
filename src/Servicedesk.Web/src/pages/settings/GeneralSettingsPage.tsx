import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { SlidersHorizontal, Globe2, Clock, Wrench, AlertTriangle } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";
import { DateTimePicker } from "@/components/ui/datetime-picker";
import { settingsApi, type SettingEntry } from "@/lib/api";
import { useServerTime } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";

const APP_QUERY_KEY = ["settings", "list", "App"] as const;
const MAINTENANCE_QUERY_KEY = ["system", "maintenance"] as const;

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

function formatOffset(minutes: number): string {
  const sign = minutes >= 0 ? "+" : "-";
  const abs = Math.abs(minutes);
  const h = Math.floor(abs / 60)
    .toString()
    .padStart(2, "0");
  const m = (abs % 60).toString().padStart(2, "0");
  return `UTC${sign}${h}:${m}`;
}

export function GeneralSettingsPage() {
  const appSettings = useQuery({
    queryKey: APP_QUERY_KEY,
    queryFn: () => settingsApi.list("App"),
  });
  const { time } = useServerTime();

  const timeZoneEntry = findEntry(appSettings.data, "App.TimeZone");

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <SlidersHorizontal className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">General</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            App-wide knobs that don't belong to a single feature — localization, branding
            and display preferences.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <section className="glass-card p-6">
        <div className="mb-4 flex items-center gap-3">
          <div className="rounded-md bg-white/[0.04] p-2 text-primary">
            <Globe2 className="h-5 w-5" />
          </div>
          <div>
            <h2 className="text-base font-semibold text-foreground">Localization</h2>
            <p className="text-xs text-muted-foreground">
              Controls the clock shown in the UI and the server time returned by{" "}
              <code className="font-mono text-[10px]">/api/system/time</code>. Business-hours
              schedules and SLA math keep their own per-schema timezone.
            </p>
          </div>
        </div>

        {appSettings.isLoading ? (
          <Skeleton className="h-24 w-full" />
        ) : timeZoneEntry ? (
          <>
            <SettingField
              entry={timeZoneEntry}
              queryKey={APP_QUERY_KEY}
              label="Application timezone"
              hint="IANA id, e.g. Europe/Brussels, America/New_York, Asia/Tokyo. Leave empty to inherit the container's TZ env-var (set from the host timezone by install.sh). Invalid values silently fall back to the container default."
            />
            <div className="mt-4 rounded-md border border-white/[0.06] bg-white/[0.02] p-3">
              <div className="flex items-center gap-2 text-xs text-muted-foreground">
                <Clock className="h-3.5 w-3.5" />
                <span className="font-medium uppercase tracking-wider">Currently resolved</span>
              </div>
              {time ? (
                <div className="mt-1 flex flex-wrap items-baseline gap-x-3 gap-y-1 font-mono text-xs">
                  <span className="text-foreground">{time.timezone}</span>
                  <span className="text-muted-foreground">
                    ({formatOffset(time.offsetMinutes)})
                  </span>
                </div>
              ) : (
                <p className="mt-1 text-xs text-muted-foreground">Syncing with server…</p>
              )}
              <p className="mt-2 text-[11px] text-muted-foreground/70">
                The displayed value comes from the server, not your browser. A change takes
                effect immediately — all open tabs re-sync within ~2&nbsp;minutes.
              </p>
            </div>
          </>
        ) : (
          <p className="text-sm text-muted-foreground">Timezone setting not available.</p>
        )}
      </section>

      <MaintenanceWindowSection
        entries={appSettings.data}
        loading={appSettings.isLoading}
      />
    </div>
  );
}

function MaintenanceWindowSection({
  entries,
  loading,
}: {
  entries: SettingEntry[] | undefined;
  loading: boolean;
}) {
  const qc = useQueryClient();
  const { time } = useServerTime();

  const enabledEntry = findEntry(entries, "App.Maintenance.Enabled");
  const startEntry = findEntry(entries, "App.Maintenance.StartUtc");
  const endEntry = findEntry(entries, "App.Maintenance.EndUtc");
  const messageEntry = findEntry(entries, "App.Maintenance.Message");

  const enabled = enabledEntry?.value === "true";
  const startUtc = startEntry?.value || null;
  const endUtc = endEntry?.value || null;

  // Local message draft so typing doesn't fire one save per keystroke.
  const [messageDraft, setMessageDraft] = useState(messageEntry?.value ?? "");
  useEffect(() => {
    setMessageDraft(messageEntry?.value ?? "");
  }, [messageEntry?.value]);

  const offsetMinutes = time?.offsetMinutes ?? 0;

  const update = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) =>
      settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: APP_QUERY_KEY });
      qc.invalidateQueries({ queryKey: MAINTENANCE_QUERY_KEY });
    },
    onError: (_err, vars) => {
      toast.error(`Failed to update ${vars.key}`);
    },
  });

  const setEnabled = (next: boolean) => {
    update.mutate({ key: "App.Maintenance.Enabled", value: next ? "true" : "false" });
  };
  const setStart = (iso: string | null) => {
    update.mutate({ key: "App.Maintenance.StartUtc", value: iso ?? "" });
  };
  const setEnd = (iso: string | null) => {
    update.mutate({ key: "App.Maintenance.EndUtc", value: iso ?? "" });
  };
  const commitMessage = () => {
    if (messageDraft === (messageEntry?.value ?? "")) return;
    update.mutate({ key: "App.Maintenance.Message", value: messageDraft });
  };

  // Validation surfaces — soft warnings, not blocking. The banner falls back
  // to a generic copy when message is empty, and an end-time in the past
  // simply means the public endpoint reports inactive even with the toggle on.
  const endBeforeStart =
    startUtc && endUtc && Date.parse(endUtc) < Date.parse(startUtc);
  const endInPast =
    endUtc && time && Date.parse(endUtc) < Date.parse(time.utc);

  return (
    <section className="glass-card p-6">
      <div className="mb-4 flex items-start gap-3">
        <div className="rounded-md bg-white/[0.04] p-2 text-amber-300">
          <Wrench className="h-5 w-5" />
        </div>
        <div className="flex-1">
          <h2 className="text-base font-semibold text-foreground">Maintenance window</h2>
          <p className="text-xs text-muted-foreground">
            Show a warning bar app-wide and on the login page. Visible the moment the
            toggle flips on; auto-disappears once the end time has passed (server clock).
            Times are interpreted in the application timezone shown above.
          </p>
        </div>
        <ToggleSwitch
          checked={enabled}
          disabled={loading || update.isPending}
          onChange={setEnabled}
        />
      </div>

      {loading ? (
        <Skeleton className="h-32 w-full" />
      ) : (
        <div className={cn("space-y-4", !enabled && "opacity-60")}>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <FieldShell label="Start">
              <DateTimePicker
                value={startUtc}
                offsetMinutes={offsetMinutes}
                onChange={setStart}
                disabled={update.isPending}
                placeholder="Set start time"
              />
            </FieldShell>
            <FieldShell label="End">
              <DateTimePicker
                value={endUtc}
                offsetMinutes={offsetMinutes}
                onChange={setEnd}
                disabled={update.isPending}
                placeholder="Set end time"
                minUtc={startUtc}
              />
            </FieldShell>
          </div>

          <FieldShell label="Message">
            <textarea
              value={messageDraft}
              onChange={(e) => setMessageDraft(e.target.value)}
              onBlur={commitMessage}
              disabled={update.isPending}
              rows={3}
              maxLength={500}
              placeholder="e.g. We will be performing scheduled maintenance to upgrade the database. Some features may be temporarily unavailable."
              className="w-full rounded-md border border-white/10 bg-white/[0.03] px-3 py-2 text-sm text-foreground shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
            />
          </FieldShell>

          {(endBeforeStart || endInPast) && (
            <div className="flex items-start gap-2 rounded-md border border-amber-500/30 bg-amber-500/[0.08] px-3 py-2 text-xs text-amber-200">
              <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <div>
                {endBeforeStart && <p>End time is earlier than start time.</p>}
                {endInPast && (
                  <p>
                    End time is already in the past — the banner will not appear
                    until you set a future end (or clear it).
                  </p>
                )}
              </div>
            </div>
          )}
        </div>
      )}
    </section>
  );
}

function FieldShell({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block space-y-1.5">
      <span className="text-[10px] font-medium uppercase tracking-wider text-muted-foreground/70">
        {label}
      </span>
      {children}
    </label>
  );
}

function ToggleSwitch({
  checked,
  disabled,
  onChange,
}: {
  checked: boolean;
  disabled?: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cn(
        "relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
        "disabled:cursor-not-allowed disabled:opacity-50",
        checked
          ? "bg-gradient-to-r from-violet-600 to-indigo-600"
          : "bg-white/[0.08]",
      )}
    >
      <span
        className={cn(
          "pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow-lg ring-0 transition-transform duration-200 ease-in-out",
          checked ? "translate-x-5" : "translate-x-0",
        )}
      />
    </button>
  );
}
