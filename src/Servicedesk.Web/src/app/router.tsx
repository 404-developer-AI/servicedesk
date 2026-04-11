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
import { SettingsIndexPage } from "@/pages/settings/SettingsIndexPage";
import { LoginPage } from "@/pages/auth/LoginPage";
import { SetupWizardPage } from "@/pages/auth/SetupWizardPage";
import { ProfilePage } from "@/pages/profile/ProfilePage";

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
      comingIn="v0.0.5"
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
        comingIn="v0.0.5"
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

const viewsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/views",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: () => stubForPath("/views"),
});

const ticketsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/tickets",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: () => stubForPath("/tickets"),
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

const settingsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/settings",
  beforeLoad: authGate(["Admin"]),
  component: SettingsIndexPage,
});

const settingsAuditRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/settings/audit",
  beforeLoad: authGate(["Admin"]),
  component: AuditLogPage,
});

const routeTree = rootRoute.addChildren([
  loginRoute,
  setupRoute,
  dashboardRoute,
  viewsRoute,
  ticketsRoute,
  kbRoute,
  profileRoute,
  settingsRoute,
  settingsAuditRoute,
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
