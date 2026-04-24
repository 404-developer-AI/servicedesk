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
import { DashboardPage } from "@/pages/dashboard/DashboardPage";
import { AuditLogPage } from "@/pages/settings/AuditLogPage";
import { GeneralSettingsPage } from "@/pages/settings/GeneralSettingsPage";
import { HealthSettingsPage } from "@/pages/settings/HealthSettingsPage";
import { IntegrationsSettingsPage } from "@/pages/settings/IntegrationsSettingsPage";
import { MailSettingsPage } from "@/pages/settings/MailSettingsPage";
import { MailDiagnosticsPage } from "@/pages/settings/MailDiagnosticsPage";
import { SlaSettingsPage } from "@/pages/settings/SlaSettingsPage";
import { IntakeFormsSettingsPage } from "@/pages/settings/IntakeFormsSettingsPage";
import { PublicIntakeFormPage } from "@/pages/intake/PublicIntakeFormPage";
import { TicketsSettingsPage } from "@/pages/settings/TicketsSettingsPage";
import { SettingsLayout } from "@/shell/SettingsLayout";
import { LoginPage } from "@/pages/auth/LoginPage";
import { SetupWizardPage } from "@/pages/auth/SetupWizardPage";
import { ProfilePage } from "@/pages/profile/ProfilePage";
import { MentionHistoryPage } from "@/pages/profile/MentionHistoryPage";
import { ViewsSettingsPage } from "@/pages/settings/ViewsSettingsPage";
import { QueueAccessSettingsPage } from "@/pages/settings/QueueAccessSettingsPage";
import { UsersSettingsPage } from "@/pages/settings/UsersSettingsPage";
import { ViewGroupsSettingsPage } from "@/pages/settings/ViewGroupsSettingsPage";
import { CompaniesSettingsPage } from "@/pages/settings/CompaniesSettingsPage";
import { CompanyDetailPage } from "@/pages/companies/CompanyDetailPage";
import { ContactsPage } from "@/pages/contacts/ContactsPage";
import { ContactDetailPage } from "@/pages/contacts/ContactDetailPage";
import { TicketListPage } from "@/pages/tickets/TicketListPage";
import { TicketDetailPage } from "@/pages/tickets/TicketDetailPage";
import { TicketComposePage } from "@/pages/tickets/TicketComposePage";
import { SlaLogPage } from "@/pages/sla/SlaLogPage";
import { SearchPage } from "@/pages/search/SearchPage";

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

/// Routes that render OUTSIDE AppShell — no sidebar, no CriticalBanner.
/// Used for the pop-out compose window so the agent can park it next to
/// the main tab with just the form visible. Public tokenised intake-form
/// fills also land here — the customer has no session and never sees the
/// agent UI.
function isBareRoute(path: string): boolean {
  if (UNAUTHENTICATED_PATHS.has(path)) return true;
  if (path.endsWith("/compose")) return true;
  if (path.startsWith("/intake/")) return true;
  return false;
}

function RootLayout() {
  const path = useRouterState({ select: (s) => s.location.pathname });
  if (isBareRoute(path)) {
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
  beforeLoad: (ctx) => {
    anyAuthenticatedGate()(ctx);
  },
  component: DashboardPage,
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

// Pop-out compose window. Rendered outside AppShell (see RootLayout) so
// the agent can park it as a second browser window and keep the main
// tab on the activity feed.
const ticketComposeRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/tickets/$ticketId/compose",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: function TicketComposeRoute() {
    const { ticketId } = ticketComposeRoute.useParams();
    return <TicketComposePage ticketId={ticketId} />;
  },
});

const searchRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/search",
  beforeLoad: authGate(["Agent", "Admin"]),
  validateSearch: (raw: Record<string, unknown>) => ({
    q: typeof raw.q === "string" ? raw.q : undefined,
    type: typeof raw.type === "string" ? raw.type : undefined,
    offset: typeof raw.offset === "string" ? Number(raw.offset) : (raw.offset as number | undefined),
  }),
  component: SearchPage,
});

const slaLogRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/sla-log",
  beforeLoad: authGate(["Admin"]),
  component: SlaLogPage,
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

// v0.0.12 stap 4 — history of @@-mentions received by the caller.
// Agent+Admin only; customers never receive mentions in this release.
const profileMentionsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/profile/mentions",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: MentionHistoryPage,
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

// Sections are declared statically so TanStack Router can infer each literal
// path into the typed route union — needed so `redirect({ to: "/settings/general" })`
// type-checks.
const settingsGeneralRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "general",
  component: GeneralSettingsPage,
});

const settingsMailRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "mail",
  component: MailSettingsPage,
});

const settingsSlaRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "sla",
  component: SlaSettingsPage,
});

const settingsIntakeFormsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "intake-forms",
  component: IntakeFormsSettingsPage,
});

const settingsIntegrationsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations",
  component: IntegrationsSettingsPage,
});

const settingsAuditRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "audit",
  component: AuditLogPage,
});

const settingsMailDiagnosticsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "mail-diagnostics",
  component: MailDiagnosticsPage,
});

const settingsHealthRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "health",
  component: HealthSettingsPage,
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

const settingsQueueAccessRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "queue-access",
  component: QueueAccessSettingsPage,
});

const settingsUsersRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "users",
  component: UsersSettingsPage,
});

// Public tokenised intake form. Rendered outside AppShell (see RootLayout)
// so a customer without a session sees only the form. No beforeLoad gate
// because the server-side token validates the request; an invalid or
// expired token renders an in-page error state instead of redirecting.
const publicIntakeRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/intake/$token",
  component: function PublicIntakeRoute() {
    const { token } = publicIntakeRoute.useParams();
    return <PublicIntakeFormPage token={token} />;
  },
});

const settingsViewGroupsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "view-groups",
  component: ViewGroupsSettingsPage,
});

const settingsCompaniesRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "companies",
  component: CompaniesSettingsPage,
});

const companyDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/companies/$companyId",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: function CompanyDetailRoute() {
    const { companyId } = companyDetailRoute.useParams();
    return <CompanyDetailPage companyId={companyId} />;
  },
});

const settingsContactsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "contacts",
  component: ContactsPage,
});

const contactDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/contacts/$contactId",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: function ContactDetailRoute() {
    const { contactId } = contactDetailRoute.useParams();
    return <ContactDetailPage contactId={contactId} />;
  },
});

const routeTree = rootRoute.addChildren([
  loginRoute,
  setupRoute,
  dashboardRoute,
  ticketsRoute,
  ticketDetailRoute,
  ticketComposeRoute,
  companyDetailRoute,
  contactDetailRoute,
  searchRoute,
  slaLogRoute,
  kbRoute,
  profileRoute,
  profileMentionsRoute,
  settingsRoute.addChildren([
    settingsIndexRoute,
    settingsGeneralRoute,
    settingsMailRoute,
    settingsSlaRoute,
    settingsIntakeFormsRoute,
    settingsIntegrationsRoute,
    settingsTicketsRoute,
    settingsCompaniesRoute,
    settingsContactsRoute,
    settingsViewsRoute,
    settingsQueueAccessRoute,
    settingsUsersRoute,
    settingsViewGroupsRoute,
    settingsMailDiagnosticsRoute,
    settingsHealthRoute,
    settingsAuditRoute,
  ]),
  publicIntakeRoute,
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
