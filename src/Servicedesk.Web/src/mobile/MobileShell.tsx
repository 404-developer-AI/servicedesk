import * as React from "react";
import { Link, Outlet, useCanGoBack, useRouter, useRouterState } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { Toaster } from "sonner";
import { ArrowLeft, AtSign, LayoutList, Search, UserCircle2 } from "lucide-react";
import { BrandMark } from "@/components/BrandMark";
import { MaintenanceBanner } from "@/components/maintenance/MaintenanceBanner";
import { useTheme } from "@/app/ThemeProvider";
import { usePresenceConnection } from "@/hooks/usePresence";
import { useUpdateCheck } from "@/hooks/useUpdateCheck";
import { useNotificationSignalR } from "@/hooks/useNotificationSignalR";
import { useSystemVersion } from "@/hooks/useSystemVersion";
import { useServerTime, formatServerLocalClock } from "@/hooks/useServerTime";
import { settingsApi } from "@/lib/api";
import { notificationApi } from "@/lib/notification-api";
import { startRum } from "@/lib/rum";
import { cn } from "@/lib/utils";
import { HeaderContext, type HeaderState } from "./mobileHeader";

/// Mobile web shell (/m). One column, a compact header that always carries
/// the server version + server clock (global requirement), and a bottom tab
/// bar within thumb reach. Only the presence hub (ticket updates, version
/// check on reconnect) and the notification hub (mention toasts) connect —
/// the desktop's integrations / Telavox / timesheet sockets stay off.

export function MobileShell() {
  usePresenceConnection();
  useUpdateCheck();
  const notificationSettings = useQuery({
    queryKey: ["settings", "notifications"],
    queryFn: () => settingsApi.notifications(),
    staleTime: 5 * 60_000,
  });
  useNotificationSignalR((notificationSettings.data?.popupDurationSeconds ?? 10) * 1000);

  const router = useRouter();
  React.useEffect(() => {
    void startRum(router);
  }, [router]);

  const [header, setHeader] = React.useState<HeaderState>({ title: "" });
  const { family } = useTheme();

  return (
    <HeaderContext.Provider value={setHeader}>
      <div className="sd-m-app app-background flex min-h-dvh flex-col" data-testid="mobile-shell">
        <MobileHeaderBar title={header.title} back={header.back ?? null} />
        <MaintenanceBanner variant="shell" />
        <main className="flex-1 px-4 pt-3 pb-[calc(5.5rem+env(safe-area-inset-bottom))]">
          <Outlet />
        </main>
        <MobileTabBar />
        <Toaster
          theme={family === "steaan" ? "light" : "dark"}
          position="top-center"
          offset={{ top: "calc(env(safe-area-inset-top) + 64px)" }}
          mobileOffset={{ top: "calc(env(safe-area-inset-top) + 64px)" }}
        />
      </div>
    </HeaderContext.Provider>
  );
}

function MobileHeaderBar({ title, back }: { title: string; back: string | null }) {
  const router = useRouter();
  const canGoBack = useCanGoBack();
  return (
    <header className="sd-m-header sticky top-0 z-30 border-b border-glass pt-[env(safe-area-inset-top)]">
      <div className="flex h-14 items-center gap-2 px-2">
        {back ? (
          <button
            type="button"
            onClick={() => {
              // Prefer real history (keeps the list's scroll position);
              // a deep link without history falls back to the parent page.
              if (canGoBack) router.history.back();
              else void router.navigate({ to: back });
            }}
            className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-full text-foreground active:bg-glass-hover"
            aria-label="Back"
          >
            <ArrowLeft className="h-5 w-5" />
          </button>
        ) : (
          <Link to="/m" className="inline-flex h-11 w-11 shrink-0 items-center justify-center" aria-label="Home">
            <BrandMark size={28} />
          </Link>
        )}
        <h1 className="min-w-0 flex-1 truncate font-display text-[17px] font-semibold tracking-tight">
          {title}
        </h1>
        <VersionClock />
      </div>
    </header>
  );
}

/// The only per-second re-render on mobile: isolated so the ticking clock
/// never re-renders the page under it.
function VersionClock() {
  const version = useSystemVersion();
  const { time, error } = useServerTime();
  const versionLabel = version.data
    ? `v${version.data.version.split("-")[0]}`
    : version.isError
      ? "version unavailable"
      : "…";
  return (
    <div
      className="flex shrink-0 flex-col items-end pr-2 font-mono text-[10.5px] leading-tight text-muted-foreground"
      data-testid="mobile-version-clock"
    >
      <span>{versionLabel}</span>
      <span className={cn("tabular-nums", error ? "text-destructive" : "text-foreground/80")}>
        {error ? "time unavailable" : time ? formatServerLocalClock(time) : "…"}
      </span>
    </div>
  );
}

const TABS = [
  { to: "/m", label: "Views", icon: LayoutList, match: (p: string) => p === "/m" || p.startsWith("/m/views") || p.startsWith("/m/tickets") },
  { to: "/m/search", label: "Search", icon: Search, match: (p: string) => p.startsWith("/m/search") },
  { to: "/m/mentions", label: "Mentions", icon: AtSign, match: (p: string) => p.startsWith("/m/mentions") },
  { to: "/m/me", label: "Me", icon: UserCircle2, match: (p: string) => p === "/m/me" || p.startsWith("/m/me/") },
] as const;

function MobileTabBar() {
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const pending = useQuery({
    queryKey: ["notifications", "pending"],
    queryFn: () => notificationApi.listPending(),
    staleTime: 30_000,
    refetchOnWindowFocus: true,
  });
  const pendingCount = pending.data?.length ?? 0;

  return (
    <nav
      className="sd-m-tabbar fixed inset-x-0 bottom-0 z-30 border-t border-glass pb-[env(safe-area-inset-bottom)]"
      aria-label="Main"
    >
      <div className="mx-auto grid h-16 max-w-xl grid-cols-4">
        {TABS.map((tab) => {
          const active = tab.match(pathname);
          const Icon = tab.icon;
          return (
            <Link
              key={tab.to}
              to={tab.to}
              className={cn(
                "sd-m-tab relative flex flex-col items-center justify-center gap-1 text-[11px] font-medium transition-colors",
                active ? "sd-m-tab-active text-primary" : "text-muted-foreground",
              )}
              aria-current={active ? "page" : undefined}
            >
              <span className="relative">
                <Icon className="h-[22px] w-[22px]" strokeWidth={active ? 2.25 : 1.75} />
                {tab.to === "/m/mentions" && pendingCount > 0 && (
                  <span className="absolute -right-2.5 -top-1.5 min-w-[18px] rounded-full bg-destructive px-1 text-center text-[10px] font-semibold leading-[18px] text-destructive-foreground">
                    {pendingCount > 99 ? "99+" : pendingCount}
                  </span>
                )}
              </span>
              {tab.label}
            </Link>
          );
        })}
      </div>
    </nav>
  );
}
