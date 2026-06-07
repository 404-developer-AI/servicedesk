import * as React from "react";
import { Link, useRouterState, useNavigate } from "@tanstack/react-router";
import { motion } from "framer-motion";
import {
  ChevronLeft,
  ChevronRight,
  Eye,
  LayoutGrid,
  LogOut,
  Pin,
  PinOff,
  Plus,
  Settings as SettingsIcon,
  UserCircle2,
} from "lucide-react";
import ticksyMark from "@/assets/brand/ticksy.svg";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { useCurrentRole } from "@/hooks/useCurrentRole";
import { useSidebarStore } from "@/stores/useSidebarStore";
import { visibleNavItems, type NavItem } from "@/shell/navItems";
import { NewTicketDrawer } from "@/shell/NewTicketDrawer";
import { useSystemVersion } from "@/hooks/useSystemVersion";
import {
  useServerTime,
  formatServerLocalClock,
  formatServerLocalDate,
} from "@/hooks/useServerTime";
import { viewApi, type View } from "@/lib/ticket-api";
import { settingsApi, preferencesApi } from "@/lib/api";
import { RecentTickets } from "@/shell/RecentTickets";
import { NotificationsWidget } from "@/shell/NotificationsWidget";
import { GlobalSearchBar } from "@/components/search/GlobalSearchBar";
import { useAuth, authStore } from "@/auth/authStore";
import { authApi } from "@/lib/api";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";

// Timezone is intentionally not displayed — on Windows dev boxes
// `TimeZoneInfo.Local.Id` returns "Romance Standard Time" etc., which is ugly
// and inconsistent with the IANA names on the Linux host. The absolute server
// time (UTC offset already applied) is what the user actually wants to see.

export function Sidebar() {
  const role = useCurrentRole();
  const allItems = visibleNavItems(role, "main");
  const collapsed = useSidebarStore((s) => s.collapsed);
  const toggle = useSidebarStore((s) => s.toggle);
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const searchStr = useRouterState({ select: (s) => s.location.searchStr });
  const version = useSystemVersion();
  const { time, error: timeError } = useServerTime();
  const { data: views } = useQuery({
    queryKey: ["views"],
    queryFn: viewApi.list,
    staleTime: 60000,
    enabled: role === "Agent" || role === "Admin",
  });
  const { data: navSettings } = useQuery({
    queryKey: ["settings", "navigation"],
    queryFn: settingsApi.navigation,
    staleTime: 60000,
    enabled: role === "Agent" || role === "Admin",
  });
  // v0.0.60 — feature pages the user pinned out of the "Features" flyout.
  // Persisted per-user server-side, so the choice survives logout/login.
  const qc = useQueryClient();
  const { data: pinnedData } = useQuery({
    queryKey: ["preferences", "pinned-features"],
    queryFn: preferencesApi.getPinnedFeatures,
    staleTime: 60000,
    enabled: role === "Agent" || role === "Admin",
  });
  const pinnedPaths = React.useMemo(() => pinnedData?.paths ?? [], [pinnedData]);

  const pinMutation = useMutation({
    mutationFn: (paths: string[]) => preferencesApi.setPinnedFeatures(paths),
    // Optimistic so the row moves the instant the user clicks the pin.
    onMutate: async (paths) => {
      await qc.cancelQueries({ queryKey: ["preferences", "pinned-features"] });
      const prev = qc.getQueryData<{ paths: string[] }>(["preferences", "pinned-features"]);
      qc.setQueryData(["preferences", "pinned-features"], { paths });
      return { prev };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(["preferences", "pinned-features"], ctx.prev);
      toast.error("Could not save your pinned features");
    },
    onSettled: () => qc.invalidateQueries({ queryKey: ["preferences", "pinned-features"] }),
  });

  // v0.0.65 — saved views the user pinned out of the "Views" flyout so they
  // render inline. Same per-user persistence + optimistic pattern as features.
  const { data: pinnedViewsData } = useQuery({
    queryKey: ["preferences", "pinned-views"],
    queryFn: preferencesApi.getPinnedViews,
    staleTime: 60000,
    enabled: role === "Agent" || role === "Admin",
  });
  const pinnedViewIds = React.useMemo(() => pinnedViewsData?.ids ?? [], [pinnedViewsData]);

  const pinViewMutation = useMutation({
    mutationFn: (ids: string[]) => preferencesApi.setPinnedViews(ids),
    onMutate: async (ids) => {
      await qc.cancelQueries({ queryKey: ["preferences", "pinned-views"] });
      const prev = qc.getQueryData<{ ids: string[] }>(["preferences", "pinned-views"]);
      qc.setQueryData(["preferences", "pinned-views"], { ids });
      return { prev };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(["preferences", "pinned-views"], ctx.prev);
      toast.error("Could not save your pinned views");
    },
    onSettled: () => qc.invalidateQueries({ queryKey: ["preferences", "pinned-views"] }),
  });

  const activeViewId = React.useMemo(() => {
    if (pathname !== "/tickets") return null;
    const params = new URLSearchParams(searchStr);
    return params.get("viewId");
  }, [pathname, searchStr]);

  const inView = !!activeViewId;

  const { user } = useAuth();
  const items = allItems.filter((item) => {
    if (item.to === "/tickets" && (inView || (navSettings && !navSettings.showOpenTickets))) return false;
    // v0.0.35 — Timesheet is per-user opt-in. Even with the right role,
    // a user without `timesheet_enabled` or `timesheet_manager` should
    // not see the menu item.
    if (item.to === "/timesheet"
        && !user?.timesheetEnabled
        && !user?.timesheetManager) {
      return false;
    }
    // v0.0.40 polish — Knowledge Base is per-user opt-in. Same rationale
    // as Timesheet above: role gates Agent+Admin, but a user without the
    // `kb_enabled` flag never sees the entry.
    if (item.to === "/kb" && !user?.kbEnabled) {
      return false;
    }
    // v0.0.42 — Activity feed is per-user opt-in. Hides the link from
    // users whose admin has not enabled the flag; server-side endpoint
    // and SignalR hub also enforce the same gate.
    if (item.to === "/activity" && !user?.activityFeedEnabled) {
      return false;
    }
    // v0.0.52 — Assets is per-user opt-in (Tactical RMM mirror). The
    // backend route is gated by AuthorizationPolicies.RequireAgent so a
    // Customer never reaches it; the per-user flag adds the role-side
    // visibility filter.
    if (item.to === "/assets" && !user?.assetsEnabled) {
      return false;
    }
    // v0.0.59 — Orders is per-user opt-in (Adsolut ERP mirror). Same
    // rationale as Assets: role gates Agent+Admin, the `adsolut_orders_enabled`
    // flag adds the visibility filter; backend /api/orders carries RequireAgent.
    if (item.to === "/orders" && !user?.adsolutOrdersEnabled) {
      return false;
    }
    // v0.0.69 — Statistics is per-user opt-in (statistics_read). Role gates
    // Agent+Admin; the backend /api/statistics endpoints enforce the same flag.
    if (item.to === "/statistics" && !user?.statisticsRead) {
      return false;
    }
    return true;
  });

  // v0.0.60 — keep the primary nav short. Dashboard + Open Tickets stay pinned
  // inline; the remaining feature pages (SLA log, KB, Timesheet, Activity,
  // Assets, Orders) collapse into a single "Features" flyout-to-the-right once
  // more than two of them are active, so the rail never grows unwieldy.
  const PINNED_PATHS = new Set(["/", "/tickets"]);
  const corePinnedItems = items.filter((i) => PINNED_PATHS.has(i.to));
  const featureItems = items.filter((i) => !PINNED_PATHS.has(i.to));
  const collapseFeatures = featureItems.length > 2;

  // When collapsed, the user's pinned features stay inline; the rest move into
  // the flyout. Intersect against the live feature set so a pin for a feature
  // that's since been disabled is simply ignored (and never re-saved).
  const pinnedFeatureItems = featureItems.filter((i) => pinnedPaths.includes(i.to));
  const flyoutFeatureItems = featureItems.filter((i) => !pinnedPaths.includes(i.to));

  const pinFeature = (path: string) => {
    if (pinnedPaths.includes(path)) return;
    pinMutation.mutate([...pinnedPaths, path]);
  };
  const unpinFeature = (path: string) => {
    pinMutation.mutate(pinnedPaths.filter((p) => p !== path));
  };

  // v0.0.65 — split the user's accessible views into the ones they pinned
  // (rendered inline) and the rest (bundled in the "Views" flyout). Both lists
  // preserve the server's sortOrder. Intersecting against the live `views` set
  // means a pin for a since-deleted view is ignored and never re-saved; an id
  // that is no longer pinned naturally falls back into the flyout. When every
  // view is pinned the flyout list is empty and the trigger is not rendered.
  const pinnedViewSet = React.useMemo(() => new Set(pinnedViewIds), [pinnedViewIds]);
  const pinnedViews = React.useMemo(
    () => (views ?? []).filter((v) => pinnedViewSet.has(v.id)),
    [views, pinnedViewSet],
  );
  const flyoutViews = React.useMemo(
    () => (views ?? []).filter((v) => !pinnedViewSet.has(v.id)),
    [views, pinnedViewSet],
  );

  const pinView = (id: string) => {
    if (pinnedViewIds.includes(id)) return;
    pinViewMutation.mutate([...pinnedViewIds, id]);
  };
  const unpinView = (id: string) => {
    pinViewMutation.mutate(pinnedViewIds.filter((v) => v !== id));
  };

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

  const navigate = useNavigate();

  const handleLogout = async () => {
    try {
      await authApi.logout();
    } catch {
      // logout is idempotent locally even if the server call fails
    }
    authStore.patch({ user: null });
    toast.success("Signed out");
    navigate({ to: "/login" });
  };

  const canSeeSettings = role === "Admin";
  const settingsActive = pathname === "/settings" || pathname.startsWith("/settings/");
  const profileActive = pathname === "/profile";

  return (
    <motion.aside
      animate={{ width: collapsed ? 76 : 260 }}
      transition={{ type: "spring", stiffness: 220, damping: 26 }}
      className="glass-panel sticky top-3 z-20 m-3 mr-0 flex h-[calc(100vh-1.5rem)] flex-col self-start overflow-hidden"
      data-testid="app-sidebar"
    >
      <div
        className={cn(
          "flex items-center gap-3 pt-5 pb-4",
          collapsed ? "justify-center px-3" : "px-4",
        )}
      >
        <img
          src={ticksyMark}
          alt=""
          aria-hidden="true"
          draggable={false}
          className="h-9 w-9 shrink-0 select-none"
        />
        {!collapsed && (
          <div className="min-w-0">
            <div className="truncate font-display text-base font-semibold tracking-tight">Servicedesk</div>
            {/* Only show a role once a session exists. The root auth-gate keeps
                anonymous visitors out of the shell entirely, but guarding here
                too means the "Customer" fallback from useCurrentRole never
                surfaces as a badge for a logged-out user. */}
            {user && (
              <div className="truncate text-[11px] uppercase tracking-[0.18em] text-muted-foreground">{role}</div>
            )}
          </div>
        )}
      </div>

      {(role === "Agent" || role === "Admin") && user?.searchEnabled && (
        <div
          className={cn(
            "mx-3 mb-3 border-b border-glass pb-3",
            collapsed && "flex justify-center",
          )}
        >
          <GlobalSearchBar collapsed={collapsed} />
        </div>
      )}

      <nav className="flex min-h-0 flex-1 flex-col space-y-1 px-3">
        {corePinnedItems.map((item) => (
          <NavRow key={item.to} item={item} active={pathname === item.to} collapsed={collapsed} />
        ))}
        {collapseFeatures ? (
          <>
            {pinnedFeatureItems.map((item) => (
              <NavRow
                key={item.to}
                item={item}
                active={pathname === item.to}
                collapsed={collapsed}
                onUnpin={() => unpinFeature(item.to)}
              />
            ))}
            {flyoutFeatureItems.length > 0 && (
              <FeaturesFlyout
                items={flyoutFeatureItems}
                collapsed={collapsed}
                pathname={pathname}
                onPin={pinFeature}
              />
            )}
          </>
        ) : (
          featureItems.map((item) => (
            <NavRow key={item.to} item={item} active={pathname === item.to} collapsed={collapsed} />
          ))
        )}
        {views && views.length > 0 && (
          <div className="mt-2 space-y-0.5 border-t border-glass pt-2">
            {/* Views the user pinned out of the flyout — rendered inline. */}
            {pinnedViews.map((v) => (
              <ViewRow
                key={v.id}
                view={v}
                active={v.id === activeViewId}
                collapsed={collapsed}
                onSelect={() => navigate({ to: "/tickets", search: { viewId: v.id } })}
                onUnpin={() => unpinView(v.id)}
              />
            ))}
            {/* Everything not pinned is bundled here; the trigger disappears
                once the user has pinned every view. */}
            {flyoutViews.length > 0 && (
              <ViewsFlyout
                views={flyoutViews}
                collapsed={collapsed}
                activeViewId={activeViewId}
                onSelect={(id) => navigate({ to: "/tickets", search: { viewId: id } })}
                onPin={pinView}
              />
            )}
          </div>
        )}

        <RecentTickets collapsed={collapsed} />
      </nav>

      {/*
        v0.0.12 stap 4 — @@-mention notifications. Sits between the nav-area
        and the collapse button so it is always visible "linksonder boven
        collapse" per the spec. The widget itself handles its two
        presentations (expanded glass-card, collapsed icon-with-popover).
      */}
      <NotificationsWidget collapsed={collapsed} />

      {/*
        When the sidebar is collapsed, Settings + New ticket live above the
        collapse button as their own icon-only tiles, with extra bottom margin
        so they read as a distinct section rather than siblings of the toggle.
        In the expanded layout both move into the status block (see below) so
        the chrome stays compact.
      */}
      {collapsed && (
        <div className="mx-3 mb-3 flex flex-col items-center gap-1">
          {canSeeSettings && (
            <Link
              to="/settings"
              title="Settings"
              className={cn(
                "flex h-9 w-9 items-center justify-center rounded-lg border transition-colors",
                settingsActive
                  ? "border-glass-strong bg-glass-strong text-foreground"
                  : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
              )}
            >
              <SettingsIcon className="h-4 w-4" />
            </Link>
          )}
          {user && (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <button
                  type="button"
                  title={user.email}
                  className={cn(
                    "flex h-9 w-9 items-center justify-center rounded-lg border transition-colors",
                    profileActive
                      ? "border-glass-strong bg-glass-strong text-foreground"
                      : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
                  )}
                  data-testid="profile-menu-trigger"
                >
                  <UserCircle2 className="h-4 w-4" />
                </button>
              </DropdownMenuTrigger>
              <DropdownMenuContent side="right" align="end" className="w-56">
                <DropdownMenuLabel className="text-xs">
                  <div className="truncate font-medium">{user.email}</div>
                  <div className="truncate text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
                    {user.role}
                  </div>
                </DropdownMenuLabel>
                <DropdownMenuSeparator />
                <DropdownMenuItem onClick={() => navigate({ to: "/profile" })}>
                  <UserCircle2 className="mr-2 h-4 w-4" /> Profile
                </DropdownMenuItem>
                <DropdownMenuItem onClick={handleLogout}>
                  <LogOut className="mr-2 h-4 w-4" /> Sign out
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          )}
          <NewTicketDrawer>
            <button
              type="button"
              title="New ticket"
              aria-label="New ticket"
              className="flex h-9 w-9 items-center justify-center rounded-lg border border-glass bg-gradient-to-br from-accent-purple to-accent-blue text-white shadow-[0_6px_20px_-8px_hsl(var(--primary)/0.55)] transition-transform hover:scale-[1.03] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
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
        className="mx-3 mt-3 flex h-8 items-center justify-center gap-2 rounded-lg border border-glass bg-glass text-xs text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
      >
        {collapsed ? <ChevronRight className="h-3.5 w-3.5" /> : <ChevronLeft className="h-3.5 w-3.5" />}
        {!collapsed && <span>Collapse</span>}
      </button>

      <div
        className={cn(
          "mx-3 mb-3 mt-2 border-t border-glass pt-2 font-mono text-[10px] text-muted-foreground",
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
                  "flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border transition-colors",
                  settingsActive
                    ? "border-glass-strong bg-glass-strong text-foreground"
                    : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
                )}
              >
                <SettingsIcon className="h-4 w-4" />
              </Link>
            )}
            {user && (
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <button
                    type="button"
                    title={user.email}
                    className={cn(
                      "flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border transition-colors",
                      profileActive
                        ? "border-glass-strong bg-glass-strong text-foreground"
                        : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
                    )}
                    data-testid="profile-menu-trigger"
                  >
                    <UserCircle2 className="h-4 w-4" />
                  </button>
                </DropdownMenuTrigger>
                <DropdownMenuContent side="top" align="end" className="w-56">
                  <DropdownMenuLabel className="text-xs">
                    <div className="truncate font-medium">{user.email}</div>
                    <div className="truncate text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
                      {user.role}
                    </div>
                  </DropdownMenuLabel>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem onClick={() => navigate({ to: "/profile" })}>
                    <UserCircle2 className="mr-2 h-4 w-4" /> Profile
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={handleLogout}>
                    <LogOut className="mr-2 h-4 w-4" /> Sign out
                  </DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            )}
            <NewTicketDrawer>
              <button
                type="button"
                title="New ticket"
                aria-label="New ticket"
                className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-glass bg-gradient-to-br from-accent-purple to-accent-blue text-white shadow-[0_6px_18px_-8px_hsl(var(--primary)/0.55)] transition-transform hover:scale-[1.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
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

/// A single primary-nav row. Extracted so the pinned items and the inline
/// (non-collapsed) feature items render identically. When `onUnpin` is given
/// (a user-pinned feature, expanded rail), a pin-off button appears on hover.
function NavRow({
  item,
  active,
  collapsed,
  onUnpin,
}: {
  item: NavItem;
  active: boolean;
  collapsed: boolean;
  onUnpin?: () => void;
}) {
  const Icon = item.icon;
  const link = (
    <Link
      to={item.to}
      className={cn(
        "group flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-all",
        active
          ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
          : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
        collapsed && "justify-center px-2",
        onUnpin && !collapsed && "pr-9",
      )}
    >
      <Icon className={cn("h-4 w-4 shrink-0", active && "text-primary")} />
      {!collapsed && <span className="truncate">{item.label}</span>}
    </Link>
  );

  if (!onUnpin || collapsed) return link;

  return (
    <div className="group/navrow relative">
      {link}
      <button
        type="button"
        title={`Unpin ${item.label}`}
        aria-label={`Unpin ${item.label}`}
        onClick={() => onUnpin()}
        className="absolute right-1.5 top-1/2 flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-md text-muted-foreground/70 opacity-0 transition-all hover:bg-glass-strong hover:text-foreground focus-visible:opacity-100 group-hover/navrow:opacity-100"
      >
        <PinOff className="h-3.5 w-3.5" />
      </button>
    </div>
  );
}

/// v0.0.60 — single "Features" entry that opens a flyout to the right listing
/// every un-pinned feature page. Keeps the rail short when many features are
/// enabled. Each row navigates; the trailing pin button pins it inline under
/// Dashboard (persisted per-user). The trigger reads as active whenever the
/// current route is one of the contained features.
function FeaturesFlyout({
  items,
  collapsed,
  pathname,
  onPin,
}: {
  items: readonly NavItem[];
  collapsed: boolean;
  pathname: string;
  onPin: (path: string) => void;
}) {
  const [open, setOpen] = React.useState(false);
  const isFeatureActive = (to: string) =>
    pathname === to || pathname.startsWith(`${to}/`);
  const anyActive = items.some((i) => isFeatureActive(i.to));

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          title="Features"
          className={cn(
            "group flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-all",
            anyActive
              ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
              : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
            collapsed && "justify-center px-2",
          )}
        >
          <LayoutGrid className={cn("h-4 w-4 shrink-0", anyActive && "text-primary")} />
          {!collapsed && (
            <>
              <span className="truncate">Features</span>
              <ChevronRight className="ml-auto h-3.5 w-3.5 opacity-60 transition-transform group-hover:translate-x-0.5" />
            </>
          )}
        </button>
      </PopoverTrigger>
      <PopoverContent side="right" align="start" sideOffset={12} className="w-60 p-1.5">
        <div className="flex items-center justify-between px-2 pb-1 pt-0.5">
          <span className="text-[10px] font-medium uppercase tracking-widest text-muted-foreground/60">
            Features
          </span>
          <span className="text-[10px] text-muted-foreground/50">Pin to sidebar</span>
        </div>
        <div className="space-y-0.5">
          {items.map((item) => {
            const Icon = item.icon;
            const active = isFeatureActive(item.to);
            return (
              <div
                key={item.to}
                className="group/feat flex items-center gap-1 rounded-md hover:bg-glass-hover"
              >
                <Link
                  to={item.to}
                  onClick={() => setOpen(false)}
                  className={cn(
                    "flex min-w-0 flex-1 items-center gap-2 rounded-md px-2 py-1.5 text-sm transition-colors",
                    active ? "text-foreground" : "text-muted-foreground hover:text-foreground",
                  )}
                >
                  <Icon className={cn("h-4 w-4 shrink-0", active && "text-primary")} />
                  <span className="truncate">{item.label}</span>
                </Link>
                <button
                  type="button"
                  title={`Pin ${item.label} to sidebar`}
                  aria-label={`Pin ${item.label} to sidebar`}
                  onClick={() => onPin(item.to)}
                  className="mr-1 flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-muted-foreground/60 opacity-0 transition-all hover:bg-glass-strong hover:text-foreground focus-visible:opacity-100 group-hover/feat:opacity-100"
                >
                  <Pin className="h-3.5 w-3.5" />
                </button>
              </div>
            );
          })}
        </div>
      </PopoverContent>
    </Popover>
  );
}

/// v0.0.65 — a single saved-view the user pinned inline. Mirrors NavRow: the
/// row navigates to the view; when expanded a pin-off button appears on hover
/// to return it to the flyout. All views share the Eye glyph, so when collapsed
/// the row leans on its title tooltip to stay identifiable.
function ViewRow({
  view,
  active,
  collapsed,
  onSelect,
  onUnpin,
}: {
  view: View;
  active: boolean;
  collapsed: boolean;
  onSelect: () => void;
  onUnpin: () => void;
}) {
  const button = (
    <button
      type="button"
      onClick={onSelect}
      title={collapsed ? view.name : undefined}
      className={cn(
        "flex w-full items-center gap-2 rounded-lg px-3 py-1.5 text-sm transition-colors",
        active
          ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
          : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
        collapsed ? "justify-center px-2" : "pr-9",
      )}
    >
      <Eye className={cn("h-3.5 w-3.5 shrink-0", active && "text-primary")} />
      {!collapsed && <span className="truncate">{view.name}</span>}
    </button>
  );

  if (collapsed) return button;

  return (
    <div className="group/viewrow relative">
      {button}
      <button
        type="button"
        title={`Unpin ${view.name}`}
        aria-label={`Unpin ${view.name}`}
        onClick={onUnpin}
        className="absolute right-1.5 top-1/2 flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-md text-muted-foreground/70 opacity-0 transition-all hover:bg-glass-strong hover:text-foreground focus-visible:opacity-100 group-hover/viewrow:opacity-100"
      >
        <PinOff className="h-3.5 w-3.5" />
      </button>
    </div>
  );
}

/// v0.0.65 — single "Views" entry that opens a flyout to the right listing
/// every saved view the user has not pinned inline. Mirrors FeaturesFlyout:
/// each row navigates; the trailing pin button lifts the view out into the rail
/// (persisted per-user). The list scrolls past ~6 entries so the flyout never
/// grows unbounded. The trigger reads as active whenever a contained view is
/// the one currently open.
function ViewsFlyout({
  views,
  collapsed,
  activeViewId,
  onSelect,
  onPin,
}: {
  views: readonly View[];
  collapsed: boolean;
  activeViewId: string | null;
  onSelect: (id: string) => void;
  onPin: (id: string) => void;
}) {
  const [open, setOpen] = React.useState(false);
  const anyActive = views.some((v) => v.id === activeViewId);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          title="Views"
          className={cn(
            "group flex w-full items-center gap-2 rounded-lg px-3 py-1.5 text-sm transition-colors",
            anyActive
              ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
              : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
            collapsed && "justify-center px-2",
          )}
        >
          <Eye className={cn("h-3.5 w-3.5 shrink-0", anyActive && "text-primary")} />
          {!collapsed && (
            <>
              <span className="truncate">Views</span>
              <ChevronRight className="ml-auto h-3.5 w-3.5 opacity-60 transition-transform group-hover:translate-x-0.5" />
            </>
          )}
        </button>
      </PopoverTrigger>
      <PopoverContent side="right" align="start" sideOffset={12} className="w-60 p-1.5">
        <div className="flex items-center justify-between px-2 pb-1 pt-0.5">
          <span className="text-[10px] font-medium uppercase tracking-widest text-muted-foreground/60">
            Views
          </span>
          <span className="text-[10px] text-muted-foreground/50">Pin to sidebar</span>
        </div>
        <div className="max-h-48 space-y-0.5 overflow-y-auto">
          {views.map((view) => {
            const active = view.id === activeViewId;
            return (
              <div
                key={view.id}
                className="group/view flex items-center gap-1 rounded-md hover:bg-glass-hover"
              >
                <button
                  type="button"
                  onClick={() => {
                    onSelect(view.id);
                    setOpen(false);
                  }}
                  className={cn(
                    "flex min-w-0 flex-1 items-center gap-2 rounded-md px-2 py-1.5 text-sm transition-colors",
                    active ? "text-foreground" : "text-muted-foreground hover:text-foreground",
                  )}
                >
                  <Eye className={cn("h-4 w-4 shrink-0", active && "text-primary")} />
                  <span className="truncate">{view.name}</span>
                </button>
                <button
                  type="button"
                  title={`Pin ${view.name} to sidebar`}
                  aria-label={`Pin ${view.name} to sidebar`}
                  onClick={() => onPin(view.id)}
                  className="mr-1 flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-muted-foreground/60 opacity-0 transition-all hover:bg-glass-strong hover:text-foreground focus-visible:opacity-100 group-hover/view:opacity-100"
                >
                  <Pin className="h-3.5 w-3.5" />
                </button>
              </div>
            );
          })}
        </div>
      </PopoverContent>
    </Popover>
  );
}
