import { Link, Outlet, useNavigate, useRouterState } from "@tanstack/react-router";
import { Toaster } from "sonner";
import { Building2, Check, ChevronsUpDown, Eye, LifeBuoy, LogOut, Plus, Ticket, UserCircle2 } from "lucide-react";
import { BrandWordmark } from "@/components/BrandMark";
import { MaintenanceBanner } from "@/components/maintenance/MaintenanceBanner";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useTheme } from "@/app/ThemeProvider";
import { useSystemVersion } from "@/hooks/useSystemVersion";
import { formatServerLocalClock, formatServerLocalDate, useServerTime } from "@/hooks/useServerTime";
import { useUpdateCheck } from "@/hooks/useUpdateCheck";
import { portalAuthApi } from "@/lib/portal-api";
import { refreshPortalAuth } from "@/auth/portalAuth";
import { cn } from "@/lib/utils";
import { usePortalCompany, usePortalMe } from "@/portal/portalShared";
import { usePortalConfig, portalOrganisation } from "@/portal/PortalAuthLayout";

/// The authenticated customer-portal chrome: top bar (brand, nav, account),
/// centred content, status footer with the live server clock + version.
/// No agent hooks (presence, notifications, Telavox, workspace) are mounted
/// here — only the update check, so a deployed version still converges.
export function PortalShell() {
  useUpdateCheck();
  const { family } = useTheme();
  const navigate = useNavigate();
  const path = useRouterState({ select: (s) => s.location.pathname });
  const me = usePortalMe();
  const company = usePortalCompany();
  const config = usePortalConfig();
  const version = useSystemVersion();
  const { time, error: timeError } = useServerTime();

  const versionLabel = version.data ? `v${version.data.version.split("-")[0]}` : version.isError ? "version unavailable" : "…";
  const clock = time ? `${formatServerLocalDate(time)} ${formatServerLocalClock(time)}` : "…";
  const organisation = portalOrganisation(config.data);
  const impersonated = me.user?.impersonated ?? false;
  const newTicketEnabled = (config.data?.enabled ? config.data.newTicketEnabled : false) && !impersonated;

  async function signOut() {
    try {
      await portalAuthApi.logout();
    } catch {
      // ignore
    }
    await refreshPortalAuth();
    navigate({ to: "/portal/login" });
  }

  /// Ends a shadow view: revoke the session server-side (logged as
  /// impersonation ended), then close the tab the admin opened — or fall
  /// back to the login page when the browser refuses to close it.
  async function exitShadow() {
    try {
      await portalAuthApi.logout();
    } catch {
      // ignore
    }
    await refreshPortalAuth();
    window.close();
    navigate({ to: "/portal/login" });
  }

  const nav = [
    { to: "/portal", label: "My tickets", icon: Ticket, active: path === "/portal" || path.startsWith("/portal/tickets/") && !path.endsWith("/new") },
    ...(newTicketEnabled ? [{ to: "/portal/tickets/new", label: "New ticket", icon: Plus, active: path === "/portal/tickets/new" }] : []),
  ];

  return (
    <div className="app-background sd-portal relative flex min-h-screen flex-col">
      <header className="sticky top-0 z-30 border-b border-glass bg-glass backdrop-blur-xl">
        <div className="mx-auto flex h-14 w-full max-w-6xl items-center gap-4 px-4 sm:px-6">
          <Link to="/portal" className="flex items-center gap-3">
            <BrandWordmark />
            <span className="hidden rounded-full border border-glass bg-glass px-2 py-0.5 text-[10px] font-medium uppercase tracking-[0.18em] text-muted-foreground sm:inline">
              Customer portal
            </span>
          </Link>
          <nav className="ml-4 hidden items-center gap-1 sm:flex">
            {nav.map((item) => (
              <Link
                key={item.to}
                to={item.to}
                className={cn(
                  "sd-nav-row inline-flex items-center gap-2 rounded-lg px-3 py-1.5 text-sm transition-colors",
                  item.active ? "sd-nav-row-active bg-glass-strong text-foreground" : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
                )}
              >
                <item.icon className="h-4 w-4" />
                {item.label}
              </Link>
            ))}
          </nav>
          <div className="ml-auto flex items-center gap-2">
            {company.companies.length > 1 ? (
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <button
                    type="button"
                    className="flex h-9 items-center gap-2 rounded-lg border border-glass bg-glass px-2.5 text-sm text-foreground transition-colors hover:bg-glass-hover"
                    data-testid="portal-company-switcher"
                    title="Switch company"
                  >
                    <Building2 className="h-4 w-4 text-muted-foreground" />
                    <span className="max-w-[200px] truncate">{company.active?.name ?? "Company"}</span>
                    <ChevronsUpDown className="h-3.5 w-3.5 text-muted-foreground" />
                  </button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end" className="w-64">
                  <DropdownMenuLabel className="text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
                    Tickets of
                  </DropdownMenuLabel>
                  {company.companies.map((c) => (
                    <DropdownMenuItem key={c.id} onClick={() => company.select(c.id)} className="gap-2">
                      <Check className={cn("h-4 w-4", c.id === company.active?.id ? "opacity-100" : "opacity-0")} />
                      <span className="min-w-0 flex-1">
                        <span className="block truncate">{c.name}</span>
                        <span className="block text-[10px] text-muted-foreground">
                          {c.canSeeCompanyTickets ? "Ticket manager — all tickets" : "Member — your tickets"}
                        </span>
                      </span>
                    </DropdownMenuItem>
                  ))}
                </DropdownMenuContent>
              </DropdownMenu>
            ) : company.active ? (
              <span className="hidden items-center gap-1.5 text-xs text-muted-foreground md:inline-flex">
                <Building2 className="h-3.5 w-3.5" />
                {company.active.name}
              </span>
            ) : null}
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <button
                  type="button"
                  className="flex h-9 items-center gap-2 rounded-lg border border-glass bg-glass px-2.5 text-sm text-foreground transition-colors hover:bg-glass-hover"
                  data-testid="portal-account-menu"
                >
                  <UserCircle2 className="h-4 w-4 text-muted-foreground" />
                  <span className="hidden max-w-[180px] truncate sm:inline">{me.user?.displayName || me.user?.email || "Account"}</span>
                </button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" className="w-60">
                <DropdownMenuLabel className="text-xs">
                  <div className="truncate font-medium">{me.user?.displayName || me.user?.email}</div>
                  <div className="truncate text-[10px] text-muted-foreground">{me.user?.email}</div>
                </DropdownMenuLabel>
                <DropdownMenuSeparator />
                <DropdownMenuItem onClick={() => navigate({ to: "/portal" })}>
                  <Ticket className="mr-2 h-4 w-4" /> My tickets
                </DropdownMenuItem>
                {newTicketEnabled ? (
                  <DropdownMenuItem onClick={() => navigate({ to: "/portal/tickets/new" })}>
                    <Plus className="mr-2 h-4 w-4" /> New ticket
                  </DropdownMenuItem>
                ) : null}
                <DropdownMenuItem onClick={() => navigate({ to: "/portal/account" })}>
                  <UserCircle2 className="mr-2 h-4 w-4" /> Account
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem onClick={signOut}>
                  <LogOut className="mr-2 h-4 w-4" /> Sign out
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        </div>
      </header>

      {impersonated ? (
        <div
          className="sticky top-14 z-20 border-b border-amber-500/30 bg-amber-500/10 backdrop-blur-xl"
          data-testid="portal-impersonation-banner"
        >
          <div className="mx-auto flex w-full max-w-6xl flex-wrap items-center gap-x-2.5 gap-y-1 px-4 py-1.5 text-xs sm:px-6">
            <Eye className="h-3.5 w-3.5 shrink-0 text-amber-600" />
            <span className="min-w-0">
              <span className="font-medium">Viewing the portal as {me.user?.displayName || me.user?.email}</span>
              <span className="text-muted-foreground"> — read-only, nothing is sent or changed.</span>
            </span>
            <button
              type="button"
              onClick={exitShadow}
              className="ml-auto rounded-md border border-amber-500/40 bg-amber-500/15 px-2.5 py-1 text-[11px] font-medium transition-colors hover:bg-amber-500/25"
            >
              Exit view
            </button>
          </div>
        </div>
      ) : null}

      <MaintenanceBanner variant="shell" />

      <main className="mx-auto w-full max-w-6xl flex-1 px-4 py-6 sm:px-6 sm:py-8">
        <Outlet />
      </main>

      <footer className="border-t border-glass">
        <div className="mx-auto flex w-full max-w-6xl flex-wrap items-center gap-x-4 gap-y-1 px-4 py-3 font-mono text-[10px] text-muted-foreground sm:px-6">
          <span className="inline-flex items-center gap-1.5">
            <LifeBuoy className="h-3 w-3" />
            {organisation} customer portal
          </span>
          <span className="inline-flex items-center gap-1.5" data-testid="portal-version">
            <span className="inline-block h-1.5 w-1.5 rounded-full bg-primary/80 shadow-[0_0_8px_hsl(var(--primary))]" />
            {versionLabel}
          </span>
          <span className={cn("ml-auto", timeError ? "text-destructive/80" : "text-foreground/80")} data-testid="portal-server-time">
            {timeError ? "time unavailable" : clock}
          </span>
        </div>
      </footer>
      <Toaster theme={family === "steaan" ? "light" : "dark"} position="bottom-right" />
    </div>
  );
}
