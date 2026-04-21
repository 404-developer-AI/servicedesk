import { useQuery } from "@tanstack/react-query";
import { SlidersHorizontal, Globe2, Clock } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";
import { settingsApi, type SettingEntry } from "@/lib/api";
import { useServerTime } from "@/hooks/useServerTime";

const APP_QUERY_KEY = ["settings", "list", "App"] as const;

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
    </div>
  );
}
