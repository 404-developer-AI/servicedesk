import {
  createRootRoute,
  createRoute,
  createRouter,
  redirect,
  Outlet,
  useRouterState,
} from "@tanstack/react-router";
import { AppShell } from "@/shell/AppShell";
import { StubPage } from "@/shell/StubPage";
import { findNavItem } from "@/shell/navItems";
import type { Role } from "@/lib/roles";
import { authStore } from "@/auth/authStore";
import { AuditLogPage } from "@/pages/settings/AuditLogPage";
import { SettingsSectionStub } from "@/pages/settings/SettingsSectionStub";
import { TicketsSettingsPage } from "@/pages/settings/TicketsSettingsPage";
import { SettingsLayout } from "@/shell/SettingsLayout";
import { findSettingsSection } from "@/shell/settingsSections";
import { LoginPage } from "@/pages/auth/LoginPage";
import { SetupWizardPage } from "@/pages/auth/SetupWizardPage";
import { ProfilePage } from "@/pages/profile/ProfilePage";
import { ViewsSettingsPage } from "@/pages/settings/ViewsSettingsPage";
import { TicketListPage } from "@/pages/tickets/TicketListPage";
import { TicketDetailPage } from "@/pages/tickets/TicketDetailPage";

// The router reads the "current role" outside of React here (for the
// beforeLoad gate). The auth store is populated by bootstrapAuth() in
// main.tsx before the router mounts, so these reads always see real state.
function currentRole(): Role | null {
  return authStore.get().user?.role ?? null;
}

function authGate(allowed: readonly Role[]) {
  return ({ location }: { location: { pathname: string } }) => {
    const role = currentRole();
    if (role === null) {
      throw redirect({ to: "/login", search: { from: location.pathname } });
    }
    if (!allowed.includes(role)) {
      throw redirect({ to: "/" });
    }
  };
}

function anyAuthenticatedGate() {
  return ({ location }: { location: { pathname: string } }) => {
    const { user } = authStore.get();
    if (!user) {
      throw redirect({ to: "/login", search: { from: location.pathname } });
    }
  };
}

const UNAUTHENTICATED_PATHS = new Set(["/login", "/setup"]);

function RootLayout() {
  const path = useRouterState({ select: (s) => s.location.pathname });
  if (UNAUTHENTICATED_PATHS.has(path)) {
    return <Outlet />;
  }
  return <AppShell />;
}

const rootRoute = createRootRoute({
  beforeLoad: ({ location }) => {
    const { setupAvailable, user } = authStore.get();
    const path = location.pathname;
    // Hard-gate the setup wizard: it's only reachable while the users table
    // is empty. Once an admin exists, every visit to /setup bounces to /login.
    if (setupAvailable && path !== "/setup") {
      throw redirect({ to: "/setup" });
    }
    if (!setupAvailable && path === "/setup") {
      throw redirect({ to: user ? "/" : "/login" });
    }
  },
  component: RootLayout,
  notFoundComponent: () => (
    <StubPage
      title="Not found"
      description="This page does not exist (or you do not have access)."
      comingIn=""
    />
  ),
});

function stubForPath(path: string) {
  const item = findNavItem(path);
  if (!item) {
    return (
      <StubPage
        title="Not found"
        description="This page does not exist."
        comingIn=""
      />
    );
  }
  return (
    <StubPage
      title={item.label}
      description={item.description}
      comingIn={item.comingIn}
    />
  );
}

const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/login",
  component: LoginPage,
});

const setupRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/setup",
  component: SetupWizardPage,
});

const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  beforeLoad: anyAuthenticatedGate(),
  component: () => stubForPath("/"),
});

const ticketsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/tickets",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: TicketListPage,
});

const ticketDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/tickets/$ticketId",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: function TicketDetailRoute() {
    const { ticketId } = ticketDetailRoute.useParams();
    return <TicketDetailPage ticketId={ticketId} />;
  },
});

const kbRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/kb",
  beforeLoad: anyAuthenticatedGate(),
  component: () => stubForPath("/kb"),
});

const profileRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/profile",
  beforeLoad: anyAuthenticatedGate(),
  component: ProfilePage,
});

// Parent route renders the master-detail layout (secondary nav rail + Outlet).
// Each section is a child route so every category has its own URL and the
// back-button / deep-linking work naturally.
const settingsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/settings",
  beforeLoad: authGate(["Admin"]),
  component: SettingsLayout,
});

// Bare /settings bounces to the first section so the content area is never
// empty when the user clicks "Settings" in the main sidebar.
const settingsIndexRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "/",
  beforeLoad: () => {
    throw redirect({ to: "/settings/general" });
  },
  component: () => null,
});

function settingsSectionStubComponent(slug: string) {
  return function SettingsSectionStubRoute() {
    const section = findSettingsSection(slug);
    if (!section) return null;
    return <SettingsSectionStub section={section} />;
  };
}

// Stub sections are declared statically (rather than via a dynamic map over
// SETTINGS_SECTIONS) so TanStack Router can infer each literal path into the
// typed route union. Without this, `redirect({ to: "/settings/general" })`
// fails to type-check.
const settingsGeneralRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "general",
  component: settingsSectionStubComponent("general"),
});

const settingsMailRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "mail",
  component: settingsSectionStubComponent("mail"),
});

const settingsSlaRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "sla",
  component: settingsSectionStubComponent("sla"),
});

const settingsIntegrationsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations",
  component: settingsSectionStubComponent("integrations"),
});

const settingsAuditRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "audit",
  component: AuditLogPage,
});

const settingsTicketsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "tickets",
  component: TicketsSettingsPage,
});

const settingsViewsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "views",
  component: ViewsSettingsPage,
});

const routeTree = rootRoute.addChildren([
  loginRoute,
  setupRoute,
  dashboardRoute,
  ticketsRoute,
  ticketDetailRoute,
  kbRoute,
  profileRoute,
  settingsRoute.addChildren([
    settingsIndexRoute,
    settingsGeneralRoute,
    settingsMailRoute,
    settingsSlaRoute,
    settingsIntegrationsRoute,
    settingsTicketsRoute,
    settingsViewsRoute,
    settingsAuditRoute,
  ]),
]);

export const router = createRouter({
  routeTree,
  defaultPreload: "intent",
});

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
