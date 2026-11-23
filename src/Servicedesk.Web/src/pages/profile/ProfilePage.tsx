import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AtSign, ChevronRight, Users } from "lucide-react";
import { toast } from "sonner";
import { useAuth } from "@/auth/authStore";
import { TwoFactorSection } from "@/pages/profile/TwoFactorSection";
import { ChangePasswordSection } from "@/pages/profile/ChangePasswordSection";
import { useTheme } from "@/app/ThemeProvider";
import { ThemePicker } from "@/components/ThemePicker";
import { MySessionsCard } from "@/components/auth/MySessionsCard";
import { Switch } from "@/components/ui/switch";
import { preferencesApi, type NotificationMailPreferences } from "@/lib/preferences-api";
import { viewApi } from "@/lib/ticket-api";
import {
  DASHBOARD_TOKEN,
  TIMESHEET_TOKEN,
  viewToken,
} from "@/lib/postCloseRedirect";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

export function ProfilePage() {
  const { user } = useAuth();
  const { theme, setTheme } = useTheme();
  const isAgent = user?.role === "Agent" || user?.role === "Admin";

  return (
    <div className="mx-auto max-w-3xl space-y-6 py-4">
      <header className="space-y-1">
        <div className="text-[11px] uppercase tracking-[0.22em] text-muted-foreground">
          Profile
        </div>
        <h1 className="font-display text-display-sm font-semibold">
          {user?.email ?? "Profile"}
        </h1>
        <p className="text-xs text-muted-foreground">
          Your personal account settings. App-wide configuration lives under
          Settings.
        </p>
      </header>

      <section className="glass-card space-y-3 p-6">
        <div className="text-sm font-medium">Account</div>
        <dl className="grid grid-cols-1 gap-y-2 text-xs sm:grid-cols-2">
          <dt className="text-muted-foreground">Email</dt>
          <dd className="truncate">{user?.email}</dd>
          <dt className="text-muted-foreground">Role</dt>
          <dd>{user?.role}</dd>
          <dt className="text-muted-foreground">Session class</dt>
          <dd className="font-mono text-[11px] uppercase tracking-widest">{user?.amr}</dd>
        </dl>
      </section>

      <section className="glass-card space-y-4 p-6">
        <div className="flex items-baseline justify-between gap-3">
          <div className="text-sm font-medium">Appearance</div>
          <div className="text-[11px] text-muted-foreground">
            Saved to your account — applies on every device.
          </div>
        </div>
        <ThemePicker value={theme} onChange={setTheme} label="Theme" />
      </section>

      {isAgent ? <PostCloseRedirectSection /> : null}

      {isAgent ? <TagNotificationsSection /> : null}

      {isAgent ? (
        <Link
          to="/profile/mentions"
          className="glass-card flex items-center gap-3 p-4 transition-colors hover:bg-glass-hover"
        >
          <div className="flex h-9 w-9 items-center justify-center rounded-lg border border-purple-500/30 bg-purple-500/15">
            <AtSign className="h-4 w-4 text-purple-200" />
          </div>
          <div className="min-w-0 flex-1">
            <div className="text-sm font-medium">My tags</div>
            <div className="text-xs text-muted-foreground">
              Every @@-tag you have received, with its acknowledged / viewed status.
            </div>
          </div>
          <ChevronRight className="h-4 w-4 text-muted-foreground" />
        </Link>
      ) : null}

      {isAgent ? (
        <Link
          to="/profile/group-tags"
          className="glass-card flex items-center gap-3 p-4 transition-colors hover:bg-glass-hover"
        >
          <div className="flex h-9 w-9 items-center justify-center rounded-lg border border-primary/25 bg-primary/10">
            <Users className="h-4 w-4 text-primary" />
          </div>
          <div className="min-w-0 flex-1">
            <div className="text-sm font-medium">My group tags</div>
            <div className="text-xs text-muted-foreground">
              Tags for the groups you belong to, with who acknowledged them.
            </div>
          </div>
          <ChevronRight className="h-4 w-4 text-muted-foreground" />
        </Link>
      ) : null}

      {isAgent ? <MySessionsCard /> : null}

      <ChangePasswordSection />

      <TwoFactorSection />
    </div>
  );
}

/**
 * v0.1.54 — which notifications also arrive by mail. The bell entry and the
 * live toast always come; these only switch the mail copy. The app-wide mail
 * switch (Settings → Notifications) still overrides them.
 */
const MAIL_SWITCHES: { key: keyof NotificationMailPreferences; label: string; help: string }[] = [
  { key: "tags", label: "Email me when I'm tagged", help: "When someone tags you with @@ in a note, mail or reply." },
  { key: "replies", label: "Email me when someone replies to my note", help: "A reply to your internal note or to one of your replies." },
  { key: "likes", label: "Email me when someone likes my note", help: "Off by default — likes are usually best left in the bell." },
];

function TagNotificationsSection() {
  const queryClient = useQueryClient();
  const { data } = useQuery({
    queryKey: ["preferences", "notification-mail"],
    queryFn: preferencesApi.getNotificationMail,
    staleTime: 60_000,
  });
  const save = useMutation({
    mutationFn: (change: Partial<NotificationMailPreferences>) => preferencesApi.setNotificationMail(change),
    onMutate: (change) => {
      queryClient.setQueryData<NotificationMailPreferences>(["preferences", "notification-mail"],
        (old) => (old ? { ...old, ...change } : old));
    },
    onSuccess: () => toast.success("Preference saved"),
    onError: () => {
      toast.error("Failed to save the preference");
      void queryClient.invalidateQueries({ queryKey: ["preferences", "notification-mail"] });
    },
  });

  return (
    <section className="glass-card space-y-3 p-6">
      <div className="flex items-baseline justify-between gap-3">
        <div className="text-sm font-medium">Notifications by mail</div>
        <div className="text-[11px] text-muted-foreground">Saved to your account.</div>
      </div>
      <p className="text-xs text-muted-foreground">
        You always get the bell notification; these switches only control the extra mail.
      </p>
      {MAIL_SWITCHES.map((s) => (
        <label key={s.key} className="flex items-start justify-between gap-4">
          <span className="min-w-0 space-y-0.5">
            <span className="block text-sm">{s.label}</span>
            <span className="block text-xs text-muted-foreground">{s.help}</span>
          </span>
          <Switch
            checked={data?.[s.key] ?? false}
            disabled={!data || save.isPending}
            onCheckedChange={(v) => save.mutate({ [s.key]: v })}
            aria-label={s.label}
          />
        </label>
      ))}
    </section>
  );
}

/**
 * Lets an agent pick where the sidebar's "remove from Recent" (×) action
 * sends them when they dismiss the ticket they currently have open. The
 * dropdown only lists destinations this user can actually reach — the
 * dashboard, each saved view they have access to, and the timesheet when
 * their per-user timesheet flag is on — so it is impossible to pick a
 * destination outside one's own access.
 */
function PostCloseRedirectSection() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const canTimesheet = !!(user?.timesheetEnabled || user?.timesheetManager);

  const { data: pref } = useQuery({
    queryKey: ["preferences", "post-close-redirect"],
    queryFn: preferencesApi.getPostCloseRedirect,
    staleTime: 60_000,
  });
  const { data: views } = useQuery({
    queryKey: ["views"],
    queryFn: viewApi.list,
    staleTime: 60_000,
  });

  const save = useMutation({
    mutationFn: (target: string) => preferencesApi.setPostCloseRedirect(target),
    onSuccess: (res) => {
      queryClient.setQueryData(["preferences", "post-close-redirect"], res);
      toast.success("Redirect updated");
    },
    onError: () => toast.error("Failed to save redirect"),
  });

  // A stored "view:<id>" whose view is gone/inaccessible would leave the
  // Select with no matching item; coerce the displayed value back to the
  // dashboard so the trigger never renders blank.
  const stored = pref?.target ?? DASHBOARD_TOKEN;
  const value =
    stored === DASHBOARD_TOKEN ||
    (stored === TIMESHEET_TOKEN && canTimesheet) ||
    (stored.startsWith("view:") &&
      (views ?? []).some((v) => v.id === stored.slice("view:".length)))
      ? stored
      : DASHBOARD_TOKEN;

  return (
    <section className="glass-card space-y-3 p-6">
      <div className="flex items-baseline justify-between gap-3">
        <div className="text-sm font-medium">After closing a ticket</div>
        <div className="text-[11px] text-muted-foreground">
          Saved to your account.
        </div>
      </div>
      <p className="text-xs text-muted-foreground">
        When you dismiss the open ticket with the × in the sidebar's Recent
        list, go to:
      </p>
      <Select value={value} onValueChange={(v) => save.mutate(v)}>
        <SelectTrigger className="h-9 min-w-[240px]">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={DASHBOARD_TOKEN}>Dashboard</SelectItem>
          {canTimesheet && (
            <SelectItem value={TIMESHEET_TOKEN}>Timesheet</SelectItem>
          )}
          {(views ?? []).map((v) => (
            <SelectItem key={v.id} value={viewToken(v.id)}>
              {v.name}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </section>
  );
}
