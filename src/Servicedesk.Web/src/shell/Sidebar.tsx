import { Link, useRouterState } from "@tanstack/react-router";
import { motion } from "framer-motion";
import {
  ChevronLeft,
  ChevronRight,
  Eye,
  Plus,
  Settings as SettingsIcon,
  Sparkles,
} from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { cn } from "@/lib/utils";
import { useCurrentRole } from "@/hooks/useCurrentRole";
import { useSidebarStore } from "@/stores/useSidebarStore";
import { visibleNavItems } from "@/shell/navItems";
import { NewTicketDrawer } from "@/shell/NewTicketDrawer";
import { useSystemVersion } from "@/hooks/useSystemVersion";
import {
  useServerTime,
  formatServerLocalClock,
  formatServerLocalDate,
} from "@/hooks/useServerTime";
import { viewApi } from "@/lib/ticket-api";
import { RecentTickets } from "@/shell/RecentTickets";

// Timezone is intentionally not displayed — on Windows dev boxes
// `TimeZoneInfo.Local.Id` returns "Romance Standard Time" etc., which is ugly
// and inconsistent with the IANA names on the Linux host. The absolute server
// time (UTC offset already applied) is what the user actually wants to see.

export function Sidebar() {
  const role = useCurrentRole();
  const items = visibleNavItems(role, "main");
  const collapsed = useSidebarStore((s) => s.collapsed);
  const toggle = useSidebarStore((s) => s.toggle);
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const version = useSystemVersion();
  const { time, error: timeError } = useServerTime();
  const { data: views } = useQuery({
    queryKey: ["views"],
    queryFn: viewApi.list,
    staleTime: 60000,
    enabled: role === "Agent" || role === "Admin",
  });

  // Strip any MinVer pre-release suffix (e.g. "0.0.4-alpha.0.5" → "0.0.4") so
  // the UI shows a clean `vX.X.X`. Once a v0.0.4 tag is pushed there is no
  // suffix anyway; this just keeps untagged dev builds from looking noisy.
  const versionLabel = version.data
    ? `v${version.data.version.split("-")[0]}`
    : version.isError
      ? "version unavailable"
      : "…";
  const clock = time ? formatServerLocalClock(time) : "…";
  const date = time ? formatServerLocalDate(time) : "";

  const canSeeSettings = role === "Admin";
  const settingsActive = pathname === "/settings" || pathname.startsWith("/settings/");

  return (
    <motion.aside
      animate={{ width: collapsed ? 76 : 260 }}
      transition={{ type: "spring", stiffness: 220, damping: 26 }}
      className="glass-panel sticky top-3 z-20 m-3 mr-0 flex h-[calc(100vh-1.5rem)] flex-col self-start overflow-hidden"
      data-testid="app-sidebar"
    >
      <div className="flex items-center gap-3 px-4 pt-5 pb-4">
        <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-[calc(var(--radius)-4px)] bg-gradient-to-br from-accent-purple to-accent-blue shadow-[0_0_20px_-4px_hsl(var(--primary)/0.7)]">
          <Sparkles className="h-5 w-5 text-white" />
        </div>
        {!collapsed && (
          <div className="min-w-0">
            <div className="truncate font-display text-base font-semibold tracking-tight">Servicedesk</div>
            <div className="truncate text-[11px] uppercase tracking-[0.18em] text-muted-foreground">{role}</div>
          </div>
        )}
      </div>

      <nav className="flex-1 space-y-1 px-3">
        {items.map((item) => {
          const active = pathname === item.to;
          const Icon = item.icon;
          return (
            <Link
              key={item.to}
              to={item.to}
              className={cn(
                "group flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-all",
                active
                  ? "bg-white/[0.07] text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
                  : "text-muted-foreground hover:bg-white/[0.04] hover:text-foreground",
                collapsed && "justify-center px-2"
              )}
            >
              <Icon className={cn("h-4 w-4 shrink-0", active && "text-primary")} />
              {!collapsed && <span className="truncate">{item.label}</span>}
            </Link>
          );
        })}
        {!collapsed && views && views.length > 0 && (
          <div className="mt-2 border-t border-white/5 pt-2">
            <div className="px-3 pb-1 text-[10px] font-medium uppercase tracking-widest text-muted-foreground/60">
              Views
            </div>
            <div className="space-y-0.5">
              {views.slice(0, 8).map((v) => (
                <button
                  key={v.id}
                  type="button"
                  onClick={() => {
                    window.location.href = `/tickets?viewId=${v.id}`;
                  }}
                  className="flex w-full items-center gap-2 rounded-lg px-3 py-1.5 text-sm text-muted-foreground hover:bg-white/[0.04] hover:text-foreground transition-colors"
                >
                  <Eye className="h-3.5 w-3.5 shrink-0" />
                  <span className="truncate">{v.name}</span>
                </button>
              ))}
            </div>
          </div>
        )}

        <RecentTickets collapsed={collapsed} />
      </nav>

      {/*
        When the sidebar is collapsed, Settings + New ticket live above the
        collapse button as their own icon-only tiles, with extra bottom margin
        so they read as a distinct section rather than siblings of the toggle.
        In the expanded layout both move into the status block (see below) so
        the chrome stays compact.
      */}
      {collapsed && (
        <div className="mx-3 mb-3 flex flex-col gap-1">
          {canSeeSettings && (
            <Link
              to="/settings"
              title="Settings"
              className={cn(
                "flex h-9 items-center justify-center rounded-lg text-sm transition-all",
                settingsActive
                  ? "bg-white/[0.07] text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
                  : "text-muted-foreground hover:bg-white/[0.04] hover:text-foreground",
              )}
            >
              <SettingsIcon className="h-4 w-4" />
            </Link>
          )}
          <NewTicketDrawer>
            <button
              type="button"
              title="New ticket"
              aria-label="New ticket"
              className="flex h-9 items-center justify-center rounded-lg bg-gradient-to-br from-accent-purple to-accent-blue text-white shadow-[0_6px_20px_-8px_hsl(var(--primary)/0.55)] transition-transform hover:scale-[1.03] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <Plus className="h-4 w-4" />
            </button>
          </NewTicketDrawer>
        </div>
      )}

      <button
        type="button"
        onClick={toggle}
        aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
        className="mx-3 mt-3 flex h-8 items-center justify-center gap-2 rounded-lg border border-white/10 bg-white/[0.03] text-xs text-muted-foreground transition-colors hover:bg-white/[0.07] hover:text-foreground"
      >
        {collapsed ? <ChevronRight className="h-3.5 w-3.5" /> : <ChevronLeft className="h-3.5 w-3.5" />}
        {!collapsed && <span>Collapse</span>}
      </button>

      <div
        className={cn(
          "mx-3 mb-3 mt-2 border-t border-white/5 pt-2 font-mono text-[10px] text-muted-foreground",
          collapsed ? "text-center" : "",
        )}
        data-testid="sidebar-status"
      >
        {!collapsed ? (
          <div className="flex items-center gap-2">
            <div className="min-w-0 flex-1 space-y-1">
              <div className="flex items-center gap-1.5" data-testid="sidebar-version">
                <span className="inline-block h-1.5 w-1.5 shrink-0 rounded-full bg-primary/80 shadow-[0_0_8px_hsl(var(--primary))]" />
                <span className="truncate">{versionLabel}</span>
              </div>
              <div
                data-testid="sidebar-server-time"
                className={cn(
                  "truncate",
                  timeError ? "text-destructive/80" : "text-foreground/80",
                )}
              >
                {timeError ? "time unavailable" : `${date} ${clock}`}
              </div>
            </div>
            {canSeeSettings && (
              <Link
                to="/settings"
                title="Settings"
                aria-label="Settings"
                className={cn(
                  "flex h-8 w-8 shrink-0 items-center justify-center rounded-md transition-colors",
                  settingsActive
                    ? "bg-white/[0.07] text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
                    : "text-muted-foreground hover:bg-white/[0.06] hover:text-foreground",
                )}
              >
                <SettingsIcon className="h-4 w-4" />
              </Link>
            )}
            <NewTicketDrawer>
              <button
                type="button"
                title="New ticket"
                aria-label="New ticket"
                className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-gradient-to-br from-accent-purple to-accent-blue text-white shadow-[0_6px_18px_-8px_hsl(var(--primary)/0.55)] transition-transform hover:scale-[1.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <Plus className="h-4 w-4" />
              </button>
            </NewTicketDrawer>
          </div>
        ) : (
          <div className="space-y-1">
            <div
              className="flex items-center justify-center gap-1.5"
              data-testid="sidebar-version"
            >
              <span className="inline-block h-1.5 w-1.5 shrink-0 rounded-full bg-primary/80 shadow-[0_0_8px_hsl(var(--primary))]" />
            </div>
            {time && (
              <div
                data-testid="sidebar-server-time"
                className="truncate text-foreground/80"
              >
                {clock.slice(0, 5)}
              </div>
            )}
          </div>
        )}
      </div>
    </motion.aside>
  );
}
