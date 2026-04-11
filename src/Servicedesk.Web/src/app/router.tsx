import {
  createRootRoute,
  createRoute,
  createRouter,
  redirect,
} from "@tanstack/react-router";
import { AppShell } from "@/shell/AppShell";
import { StubPage } from "@/shell/StubPage";
import { findNavItem } from "@/shell/navItems";
import type { Role } from "@/lib/roles";
import { devRoleStore } from "@/stores/useDevRoleStore";

// The router reads the "current role" outside of React here (for the
// beforeLoad gate). In v0.0.4 this becomes an auth-context lookup.
function currentRole(): Role {
  return devRoleStore.get();
}

function roleGate(allowed: readonly Role[]) {
  return () => {
    const role = currentRole();
    if (!allowed.includes(role)) {
      throw redirect({ to: "/" });
    }
  };
}

const rootRoute = createRootRoute({
  component: AppShell,
  notFoundComponent: () => (
    <StubPage
      title="Not found"
      description="This page does not exist (or you do not have access)."
      comingIn="v0.0.2"
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
        comingIn="v0.0.2"
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

const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  component: () => stubForPath("/"),
});

const viewsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/views",
  beforeLoad: roleGate(["Agent", "Admin"]),
  component: () => stubForPath("/views"),
});

const ticketsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/tickets",
  beforeLoad: roleGate(["Agent", "Admin"]),
  component: () => stubForPath("/tickets"),
});

const kbRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/kb",
  component: () => stubForPath("/kb"),
});

const profileRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/profile",
  component: () => stubForPath("/profile"),
});

const settingsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/settings",
  beforeLoad: roleGate(["Admin"]),
  component: () => stubForPath("/settings"),
});

const routeTree = rootRoute.addChildren([
  dashboardRoute,
  viewsRoute,
  ticketsRoute,
  kbRoute,
  profileRoute,
  settingsRoute,
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
