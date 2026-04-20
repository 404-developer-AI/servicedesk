import { Fragment, useEffect } from "react";
import { Link, Outlet, useRouterState } from "@tanstack/react-router";
import { cn } from "@/lib/utils";
import { SETTINGS_SECTIONS } from "@/shell/settingsSections";
import { useSecondarySidebarStore } from "@/stores/useSecondarySidebarStore";

// Mounts a secondary nav rail into AppShell's root flex row (via
// useSecondarySidebarStore), so the rail shares the main Sidebar's
// `m-3 h-[calc(100vh-1.5rem)]` frame and matches its top/bottom edges
// exactly. The right-hand column just renders the <Outlet />.
export function SettingsLayout() {
  const setSecondary = useSecondarySidebarStore((s) => s.set);

  useEffect(() => {
    setSecondary(<SettingsRail />);
    return () => setSecondary(null);
  }, [setSecondary]);

  return (
    <div className="py-4">
      <Outlet />
    </div>
  );
}

function SettingsRail() {
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  return (
    <aside
      className="glass-panel sticky top-3 z-20 m-3 mr-0 flex h-[calc(100vh-1.5rem)] w-64 shrink-0 flex-col self-start overflow-hidden"
      data-testid="settings-sidebar"
    >
      <div className="px-5 pt-5 pb-4">
        <div className="font-display text-lg font-semibold tracking-tight text-foreground">
          Settings
        </div>
        <div className="mt-0.5 text-[11px] uppercase tracking-[0.18em] text-muted-foreground">
          Admin
        </div>
      </div>

      <nav className="flex-1 space-y-1 px-3 pb-4">
        {SETTINGS_SECTIONS.map((section) => {
          const to = `/settings/${section.slug}`;
          const active = pathname === to || pathname.startsWith(`${to}/`);
          const Icon = section.icon;
          return (
            <Fragment key={section.slug}>
              <Link
                to={to}
                className={cn(
                  "group flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-all",
                  active
                    ? "bg-white/[0.07] text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
                    : "text-muted-foreground hover:bg-white/[0.04] hover:text-foreground",
                )}
                data-testid={`settings-nav-${section.slug}`}
              >
                <Icon
                  className={cn(
                    "h-4 w-4 shrink-0",
                    active && "text-primary",
                  )}
                />
                <span className="truncate">{section.label}</span>
              </Link>
              {section.separatorAfter && (
                <div
                  aria-hidden
                  className="mx-3 my-1 h-px bg-white/[0.06]"
                />
              )}
            </Fragment>
          );
        })}
      </nav>
    </aside>
  );
}
