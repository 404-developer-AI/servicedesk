import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  SlidersHorizontal,
  Globe2,
  Clock,
  Wrench,
  AlertTriangle,
  Megaphone,
  Info,
  AlertCircle,
  Palette,
  Sun,
  Moon,
  Sparkles,
  Search,
  RefreshCw,
} from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";
import { DateTimePicker } from "@/components/ui/datetime-picker";
import { renderLoginBannerPreviewHtml } from "@/components/auth/LoginBanner";
import { settingsApi, type SettingEntry, type LoginBannerType } from "@/lib/api";
import { KIND_ORDER, labelForKind } from "@/components/search/searchMeta";
import { useServerTime } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";

const APP_QUERY_KEY = ["settings", "list", "App"] as const;
const UI_QUERY_KEY = ["settings", "list", "Ui"] as const;
const COPILOT_QUERY_KEY = ["settings", "list", "Copilot"] as const;
const SEARCH_QUERY_KEY = ["settings", "list", "Search"] as const;
const DEFAULT_THEME_QUERY_KEY = ["system", "default-theme"] as const;
const MAINTENANCE_QUERY_KEY = ["system", "maintenance"] as const;
const LOGIN_BANNER_QUERY_KEY = ["system", "login-banner"] as const;

const LOGIN_BANNER_MESSAGE_MAX = 500;
const LOGIN_BANNER_TYPES: ReadonlyArray<{ value: LoginBannerType; label: string }> = [
  { value: "info", label: "Info" },
  { value: "warning", label: "Warning" },
  { value: "error", label: "Error" },
];

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
  const uiSettings = useQuery({
    queryKey: UI_QUERY_KEY,
    queryFn: () => settingsApi.list("Ui"),
  });
  const copilotSettings = useQuery({
    queryKey: COPILOT_QUERY_KEY,
    queryFn: () => settingsApi.list("Copilot"),
  });
  const searchSettings = useQuery({
    queryKey: SEARCH_QUERY_KEY,
    queryFn: () => settingsApi.list("Search"),
  });
  const { time } = useServerTime();

  const timeZoneEntry = findEntry(appSettings.data, "App.TimeZone");
  const defaultThemeEntry = findEntry(uiSettings.data, "Ui.DefaultTheme");

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
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <DefaultThemeSection
        entry={defaultThemeEntry}
        loading={uiSettings.isLoading}
      />

      <section className="glass-card p-6">
        <div className="mb-4 flex items-center gap-3">
          <div className="rounded-md bg-glass p-2 text-primary">
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
            <div className="mt-4 rounded-md border border-glass-strong bg-glass p-3">
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

      <UpdateRolloutSection
        entries={appSettings.data}
        loading={appSettings.isLoading}
      />

      <LoginBannerSection
        entries={appSettings.data}
        loading={appSettings.isLoading}
      />

      <GlobalSearchSection
        entries={searchSettings.data}
        loading={searchSettings.isLoading}
      />

      <CopilotLauncherSection
        entries={copilotSettings.data}
        loading={copilotSettings.isLoading}
      />
    </div>
  );
}

const QUICK_SOURCES_FALLBACK = ["tickets", "companies", "contacts"];

function GlobalSearchSection({
  entries,
  loading,
}: {
  entries: SettingEntry[] | undefined;
  loading: boolean;
}) {
  const qc = useQueryClient();

  const quickSourcesEntry = findEntry(entries, "Search.QuickSources");
  const minLenEntry = findEntry(entries, "Search.MinQueryLength");
  const dropdownLimitEntry = findEntry(entries, "Search.DropdownLimit");
  const debounceEntry = findEntry(entries, "Search.DebounceMs");
  const timeoutEntry = findEntry(entries, "Search.SourceTimeoutMs");

  const selected = useMemo(() => {
    const raw = (quickSourcesEntry?.value ?? "")
      .split(",")
      .map((k) => k.trim())
      .filter((k) => k.length > 0);
    return new Set(raw.length > 0 ? raw : QUICK_SOURCES_FALLBACK);
  }, [quickSourcesEntry?.value]);

  const update = useMutation({
    mutationFn: (value: string) => settingsApi.update("Search.QuickSources", value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: SEARCH_QUERY_KEY });
      toast.success("Quick-search sources updated");
    },
    onError: () => {
      toast.error("Failed to update quick-search sources");
    },
  });

  const toggleKind = (kind: string) => {
    const next = new Set(selected);
    if (next.has(kind)) {
      // Never allow zero sources — an empty value would fall back to the
      // server default anyway, which is confusing to present in the UI.
      if (next.size === 1) return;
      next.delete(kind);
    } else {
      next.add(kind);
    }
    update.mutate(KIND_ORDER.filter((k) => next.has(k)).join(","));
  };

  return (
    <section className="glass-card p-6">
      <div className="mb-4 flex items-start gap-3">
        <div className="rounded-md bg-glass p-2 text-primary">
          <Search className="h-5 w-5" />
        </div>
        <div className="flex-1">
          <h2 className="text-base font-semibold text-foreground">Global search</h2>
          <p className="text-xs text-muted-foreground">
            The quick-search dropdown (top-left, Ctrl+K) only queries the sources selected
            below, so it stays instant — every source remains available on the full search
            page. Each source also runs inside its own time budget: one slow source can
            never stall the whole search.
          </p>
        </div>
      </div>

      {loading ? (
        <Skeleton className="h-48 w-full" />
      ) : (
        <div>
          <div className="mb-4">
            <p className="text-sm font-medium text-foreground">Quick-search sources</p>
            <p className="mb-2 text-[10px] uppercase tracking-wider text-muted-foreground/40 font-mono">
              Search.QuickSources
            </p>
            <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2 lg:grid-cols-3">
              {KIND_ORDER.map((kind) => {
                const checked = selected.has(kind);
                return (
                  <label
                    key={kind}
                    className={cn(
                      "flex cursor-pointer items-center gap-2 rounded-md border px-3 py-2 text-sm transition-colors",
                      checked
                        ? "border-primary/40 bg-primary/10 text-foreground"
                        : "border-glass bg-glass text-muted-foreground hover:text-foreground",
                      update.isPending && "cursor-not-allowed opacity-60",
                    )}
                  >
                    <input
                      type="checkbox"
                      checked={checked}
                      disabled={update.isPending}
                      onChange={() => toggleKind(kind)}
                      className="h-3.5 w-3.5 accent-primary"
                    />
                    <span className="truncate">{labelForKind(kind)}</span>
                  </label>
                );
              })}
            </div>
          </div>

          {minLenEntry && (
            <SettingField entry={minLenEntry} queryKey={SEARCH_QUERY_KEY} label="Minimum query length" />
          )}
          {dropdownLimitEntry && (
            <SettingField entry={dropdownLimitEntry} queryKey={SEARCH_QUERY_KEY} label="Dropdown hits per source" />
          )}
          {debounceEntry && (
            <SettingField entry={debounceEntry} queryKey={SEARCH_QUERY_KEY} label="Typing debounce (ms)" />
          )}
          {timeoutEntry && (
            <SettingField entry={timeoutEntry} queryKey={SEARCH_QUERY_KEY} label="Per-source time budget (ms)" />
          )}
        </div>
      )}
    </section>
  );
}

function CopilotLauncherSection({
  entries,
  loading,
}: {
  entries: SettingEntry[] | undefined;
  loading: boolean;
}) {
  const enabledEntry = findEntry(entries, "Copilot.Enabled");
  const urlEntry = findEntry(entries, "Copilot.Url");
  const labelEntry = findEntry(entries, "Copilot.Label");
  const popupEntry = findEntry(entries, "Copilot.OpenInPopup");

  return (
    <section className="glass-card p-6">
      <div className="mb-4 flex items-start gap-3">
        <div className="rounded-md bg-glass p-2 text-violet-300">
          <Sparkles className="h-5 w-5" />
        </div>
        <div className="flex-1">
          <h2 className="text-base font-semibold text-foreground">Copilot launcher</h2>
          <p className="text-xs text-muted-foreground">
            Adds a shortcut in the navigation (agents and admins only) that opens the real
            Microsoft Copilot in a separate window — agents land there signed in with their
            existing Microsoft 365 session. It is a pure launcher:{" "}
            <span className="font-medium text-foreground">no API key, no data from this app
            is ever sent to Copilot</span>. Copilot cannot be embedded inline because
            Microsoft blocks framing its pages, so it opens as its own window.
          </p>
        </div>
      </div>

      {loading ? (
        <Skeleton className="h-40 w-full" />
      ) : enabledEntry && urlEntry && labelEntry && popupEntry ? (
        <div>
          <SettingField
            entry={enabledEntry}
            queryKey={COPILOT_QUERY_KEY}
            label="Show Copilot button"
            hint="Master switch. When off the navigation button is hidden for everyone."
          />
          <SettingField
            entry={urlEntry}
            queryKey={COPILOT_QUERY_KEY}
            label="Copilot URL"
            hint="The URL the button opens. Defaults to the Microsoft 365 Copilot Chat entry point. Paste the exact URL your browser shows while in Copilot to pin a specific surface."
          />
          <SettingField
            entry={labelEntry}
            queryKey={COPILOT_QUERY_KEY}
            label="Button label"
            hint="Text shown on the navigation button — e.g. Copilot, AI Chat, Microsoft Copilot."
          />
          <SettingField
            entry={popupEntry}
            queryKey={COPILOT_QUERY_KEY}
            label="Open as side-panel popup"
            hint="On: opens a focused, side-panel-sized popup window pinned to the right of the screen. Off: opens a normal new browser tab. Either way the same window is reused on later clicks."
          />
        </div>
      ) : (
        <p className="text-sm text-muted-foreground">Copilot settings not available.</p>
      )}
    </section>
  );
}

function DefaultThemeSection({
  entry,
  loading,
}: {
  entry: SettingEntry | undefined;
  loading: boolean;
}) {
  const qc = useQueryClient();
  const value = (entry?.value === "dark" ? "dark" : "light") as "light" | "dark";

  const update = useMutation({
    mutationFn: (next: "light" | "dark") =>
      settingsApi.update("Ui.DefaultTheme", next),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: UI_QUERY_KEY });
      qc.invalidateQueries({ queryKey: DEFAULT_THEME_QUERY_KEY });
      toast.success("Default theme updated");
    },
    onError: () => {
      toast.error("Failed to update default theme");
    },
  });

  return (
    <section className="glass-card p-6">
      <div className="mb-4 flex items-start gap-3">
        <div className="rounded-md bg-glass p-2 text-primary">
          <Palette className="h-5 w-5" />
        </div>
        <div className="flex-1">
          <h2 className="text-base font-semibold text-foreground">Default theme</h2>
          <p className="text-xs text-muted-foreground">
            Applies to new users and to existing users who have not yet picked a theme on
            their Profile page. Once a user makes an explicit choice, their preference
            follows them across devices and overrides this default.
          </p>
        </div>
      </div>

      {loading ? (
        <Skeleton className="h-12 w-48" />
      ) : (
        <div
          role="radiogroup"
          aria-label="Default theme"
          className="inline-flex rounded-lg border border-glass bg-glass p-1"
        >
          <button
            type="button"
            role="radio"
            aria-checked={value === "light"}
            disabled={update.isPending}
            onClick={() => update.mutate("light")}
            className={cn(
              "flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium transition-colors",
              "disabled:cursor-not-allowed disabled:opacity-50",
              value === "light"
                ? "bg-primary text-primary-foreground shadow-sm"
                : "text-muted-foreground hover:text-foreground",
            )}
          >
            <Sun className="h-3.5 w-3.5" />
            Light
          </button>
          <button
            type="button"
            role="radio"
            aria-checked={value === "dark"}
            disabled={update.isPending}
            onClick={() => update.mutate("dark")}
            className={cn(
              "flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium transition-colors",
              "disabled:cursor-not-allowed disabled:opacity-50",
              value === "dark"
                ? "bg-primary text-primary-foreground shadow-sm"
                : "text-muted-foreground hover:text-foreground",
            )}
          >
            <Moon className="h-3.5 w-3.5" />
            Dark
          </button>
        </div>
      )}
    </section>
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
        <div className="rounded-md bg-glass p-2 text-amber-300">
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
              className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
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

function LoginBannerSection({
  entries,
  loading,
}: {
  entries: SettingEntry[] | undefined;
  loading: boolean;
}) {
  const qc = useQueryClient();

  const enabledEntry = findEntry(entries, "App.LoginBanner.Enabled");
  const typeEntry = findEntry(entries, "App.LoginBanner.Type");
  const messageEntry = findEntry(entries, "App.LoginBanner.Message");

  const enabled = enabledEntry?.value === "true";
  const rawType = (typeEntry?.value ?? "info").toLowerCase();
  const type: LoginBannerType =
    rawType === "warning" || rawType === "error" ? rawType : "info";

  // Local message draft so each keystroke does not fire a save; persisted
  // on blur. Same pattern as the maintenance-message field above.
  const [messageDraft, setMessageDraft] = useState(messageEntry?.value ?? "");
  useEffect(() => {
    setMessageDraft(messageEntry?.value ?? "");
  }, [messageEntry?.value]);

  const update = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) =>
      settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: APP_QUERY_KEY });
      qc.invalidateQueries({ queryKey: LOGIN_BANNER_QUERY_KEY });
    },
    onError: (_err, vars) => {
      toast.error(`Failed to update ${vars.key}`);
    },
  });

  const setEnabled = (next: boolean) => {
    update.mutate({ key: "App.LoginBanner.Enabled", value: next ? "true" : "false" });
  };
  const setType = (next: LoginBannerType) => {
    update.mutate({ key: "App.LoginBanner.Type", value: next });
  };
  const commitMessage = () => {
    if (messageDraft === (messageEntry?.value ?? "")) return;
    update.mutate({ key: "App.LoginBanner.Message", value: messageDraft });
  };

  const previewHtml = useMemo(
    () => renderLoginBannerPreviewHtml(messageDraft),
    [messageDraft],
  );
  const showPreview = enabled && previewHtml.length > 0;

  return (
    <section className="glass-card p-6">
      <div className="mb-4 flex items-start gap-3">
        <div className="rounded-md bg-glass p-2 text-sky-300">
          <Megaphone className="h-5 w-5" />
        </div>
        <div className="flex-1">
          <h2 className="text-base font-semibold text-foreground">Login banner</h2>
          <p className="text-xs text-muted-foreground">
            Show a notice above the login card on every anonymous auth page. Use{" "}
            <span className="font-medium text-foreground">Info</span> for neutral
            announcements, <span className="font-medium text-foreground">Warning</span>{" "}
            when attention is needed, and{" "}
            <span className="font-medium text-foreground">Error</span> for active
            operational issues. Limited Markdown is supported (
            <code className="font-mono text-[10px]">**bold**</code>,{" "}
            <code className="font-mono text-[10px]">_italic_</code>,{" "}
            <code className="font-mono text-[10px]">[text](https://…)</code>).
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
          <FieldShell label="Type">
            <div className="flex flex-wrap gap-2">
              {LOGIN_BANNER_TYPES.map((t) => (
                <BannerTypeButton
                  key={t.value}
                  value={t.value}
                  label={t.label}
                  selected={type === t.value}
                  disabled={update.isPending}
                  onSelect={setType}
                />
              ))}
            </div>
          </FieldShell>

          <FieldShell label="Message">
            <textarea
              value={messageDraft}
              onChange={(e) => setMessageDraft(e.target.value)}
              onBlur={commitMessage}
              disabled={update.isPending}
              rows={3}
              maxLength={LOGIN_BANNER_MESSAGE_MAX}
              placeholder="e.g. Scheduled upgrade Saturday 21:00 — login may be briefly unavailable."
              className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
            />
            <div className="mt-1 flex items-center justify-between text-[10px] text-muted-foreground/70">
              <span>
                Links open in a new tab and are sanitized — only http/https/mailto
                are kept.
              </span>
              <span className={cn(messageDraft.length >= LOGIN_BANNER_MESSAGE_MAX && "text-amber-300")}>
                {messageDraft.length}/{LOGIN_BANNER_MESSAGE_MAX}
              </span>
            </div>
          </FieldShell>

          <FieldShell label="Preview">
            {showPreview ? (
              <LoginBannerPreview type={type} html={previewHtml} />
            ) : (
              <div className="rounded-md border border-dashed border-glass bg-glass px-3 py-4 text-center text-[11px] text-muted-foreground/70">
                {enabled
                  ? "Enter a message to see the banner preview."
                  : "Banner is disabled — turn the toggle on to preview."}
              </div>
            )}
          </FieldShell>
        </div>
      )}
    </section>
  );
}

function UpdateRolloutSection({
  entries,
  loading,
}: {
  entries: SettingEntry[] | undefined;
  loading: boolean;
}) {
  const qc = useQueryClient();

  const modeEntry = findEntry(entries, "App.UpdateRefresh.Mode");
  const focusEntry = findEntry(entries, "App.UpdateRefresh.CheckOnFocus");
  const mode = modeEntry?.value === "banner" ? "banner" : "auto";

  const update = useMutation({
    mutationFn: (value: string) => settingsApi.update("App.UpdateRefresh.Mode", value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: APP_QUERY_KEY });
    },
    onError: () => {
      toast.error("Failed to update App.UpdateRefresh.Mode");
    },
  });

  return (
    <section className="glass-card p-6">
      <div className="mb-4 flex items-start gap-3">
        <div className="rounded-md bg-glass p-2 text-emerald-300">
          <RefreshCw className="h-5 w-5" />
        </div>
        <div className="flex-1">
          <h2 className="text-base font-semibold text-foreground">Update rollout</h2>
          <p className="text-xs text-muted-foreground">
            How already-open sessions pick up a deployed update. Sessions notice a new
            server version seconds after the deploy (their realtime connection drops and
            reconnects) and either reload themselves or show a persistent "new version"
            toast. Reloading is painless: users stay logged in, and a reload is skipped
            while a dialog is open or someone is typing. Either way the server refuses
            writes from outdated sessions, so a missed reload can never corrupt data.
          </p>
        </div>
      </div>

      {loading ? (
        <Skeleton className="h-24 w-full" />
      ) : (
        <div className="space-y-4">
          <FieldShell label="When a new version is detected">
            <div className="flex flex-wrap gap-2" role="radiogroup">
              <UpdateModeButton
                value="auto"
                label="Reload automatically"
                selected={mode === "auto"}
                disabled={update.isPending}
                onSelect={(v) => update.mutate(v)}
              />
              <UpdateModeButton
                value="banner"
                label="Banner only"
                selected={mode === "banner"}
                disabled={update.isPending}
                onSelect={(v) => update.mutate(v)}
              />
            </div>
            <p className="mt-1 text-[10px] text-muted-foreground/70">
              Automatic reloads that would interrupt (open dialog, active editor) fall
              back to the banner for that session.
            </p>
          </FieldShell>

          {focusEntry ? (
            <SettingField
              entry={focusEntry}
              queryKey={APP_QUERY_KEY}
              label="Also re-check when a tab regains focus"
              hint="Catches sessions that slept through the deploy, e.g. a laptop that was closed. Throttled to at most one check per 30 seconds."
            />
          ) : null}
        </div>
      )}
    </section>
  );
}

function UpdateModeButton({
  value,
  label,
  selected,
  disabled,
  onSelect,
}: {
  value: "auto" | "banner";
  label: string;
  selected: boolean;
  disabled?: boolean;
  onSelect: (next: "auto" | "banner") => void;
}) {
  const Icon = value === "auto" ? RefreshCw : Megaphone;
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      disabled={disabled}
      onClick={() => onSelect(value)}
      className={cn(
        "inline-flex items-center gap-2 rounded-md border px-3 py-1.5 text-xs font-medium transition-colors",
        "focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
        "disabled:cursor-not-allowed disabled:opacity-50",
        selected
          ? "border-emerald-400/60 bg-emerald-100 text-emerald-900 dark:border-emerald-500/40 dark:bg-emerald-500/[0.12] dark:text-emerald-100"
          : "border-glass bg-glass text-muted-foreground hover:text-foreground",
      )}
    >
      <Icon
        className={cn(
          "h-3.5 w-3.5",
          selected ? "text-emerald-600 dark:text-emerald-300" : "opacity-70",
        )}
      />
      {label}
    </button>
  );
}

function BannerTypeButton({
  value,
  label,
  selected,
  disabled,
  onSelect,
}: {
  value: LoginBannerType;
  label: string;
  selected: boolean;
  disabled?: boolean;
  onSelect: (next: LoginBannerType) => void;
}) {
  const palette = LOGIN_BANNER_PREVIEW_PALETTES[value];
  const Icon = palette.icon;
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      disabled={disabled}
      onClick={() => onSelect(value)}
      className={cn(
        "inline-flex items-center gap-2 rounded-md border px-3 py-1.5 text-xs font-medium transition-colors",
        "focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
        "disabled:cursor-not-allowed disabled:opacity-50",
        selected
          ? cn(palette.selectedBorder, palette.selectedBg, palette.selectedText)
          : "border-glass bg-glass text-muted-foreground hover:text-foreground",
      )}
    >
      <Icon className={cn("h-3.5 w-3.5", selected ? palette.selectedIcon : "opacity-70")} />
      {label}
    </button>
  );
}

function LoginBannerPreview({ type, html }: { type: LoginBannerType; html: string }) {
  const palette = LOGIN_BANNER_PREVIEW_PALETTES[type];
  const Icon = palette.icon;
  const wrapper = useMemo(() => ({ __html: html }), [html]);
  return (
    <div
      role="status"
      className={cn(
        "flex items-start gap-3 rounded-[var(--radius)] border px-4 py-3 text-xs backdrop-blur",
        palette.previewContainer,
      )}
    >
      <Icon className={cn("mt-0.5 h-4 w-4 shrink-0", palette.previewIcon)} />
      <div className="space-y-1 min-w-0">
        <p
          className={cn(
            "font-medium uppercase tracking-wider text-[10px]",
            palette.previewLabel,
          )}
        >
          {palette.title}
        </p>
        <div
          className={cn("leading-snug break-words", palette.previewBody)}
          dangerouslySetInnerHTML={wrapper}
        />
      </div>
    </div>
  );
}

// Mirror of the production banner palette — kept locally so the preview
// renders 1:1 without needing the live banner component (which queries the
// server). Update both in lock-step when tweaking colours.
const LOGIN_BANNER_PREVIEW_PALETTES: Record<
  LoginBannerType,
  {
    icon: typeof Info;
    title: string;
    selectedBorder: string;
    selectedBg: string;
    selectedText: string;
    selectedIcon: string;
    previewContainer: string;
    previewIcon: string;
    previewLabel: string;
    previewBody: string;
  }
> = {
  info: {
    icon: Info,
    title: "Notice",
    selectedBorder: "border-sky-400/60 dark:border-sky-500/40",
    selectedBg: "bg-sky-100 dark:bg-sky-500/[0.12]",
    selectedText: "text-sky-900 dark:text-sky-100",
    selectedIcon: "text-sky-600 dark:text-sky-300",
    previewContainer:
      "border-sky-400/60 bg-sky-50 dark:border-sky-500/30 dark:bg-sky-500/[0.08]",
    previewIcon: "text-sky-600 dark:text-sky-300",
    previewLabel: "text-sky-700 dark:text-sky-200/90",
    previewBody:
      "text-sky-900 [&_a]:underline [&_a]:underline-offset-2 [&_a]:text-sky-700 dark:text-sky-100/90 dark:[&_a]:text-sky-100",
  },
  warning: {
    icon: AlertTriangle,
    title: "Warning",
    selectedBorder: "border-amber-400/60 dark:border-amber-500/40",
    selectedBg: "bg-amber-100 dark:bg-amber-500/[0.12]",
    selectedText: "text-amber-900 dark:text-amber-100",
    selectedIcon: "text-amber-600 dark:text-amber-300",
    previewContainer:
      "border-amber-400/60 bg-amber-50 dark:border-amber-500/30 dark:bg-amber-500/[0.08]",
    previewIcon: "text-amber-600 dark:text-amber-300",
    previewLabel: "text-amber-700 dark:text-amber-200/90",
    previewBody:
      "text-amber-900 [&_a]:underline [&_a]:underline-offset-2 [&_a]:text-amber-700 dark:text-amber-100/90 dark:[&_a]:text-amber-100",
  },
  error: {
    icon: AlertCircle,
    title: "Error",
    selectedBorder: "border-rose-400/60 dark:border-rose-500/40",
    selectedBg: "bg-rose-100 dark:bg-rose-500/[0.14]",
    selectedText: "text-rose-900 dark:text-rose-100",
    selectedIcon: "text-rose-600 dark:text-rose-300",
    previewContainer:
      "border-rose-400/60 bg-rose-50 dark:border-rose-500/40 dark:bg-rose-500/[0.10]",
    previewIcon: "text-rose-600 dark:text-rose-300",
    previewLabel: "text-rose-700 dark:text-rose-200/90",
    previewBody:
      "text-rose-900 [&_a]:underline [&_a]:underline-offset-2 [&_a]:text-rose-700 dark:text-rose-100/90 dark:[&_a]:text-rose-100",
  },
};

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
          : "bg-glass-strong",
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
