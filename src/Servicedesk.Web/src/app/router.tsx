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
import type { Role } from "@/lib/roles";
import { authStore } from "@/auth/authStore";
import { DashboardPage } from "@/pages/dashboard/DashboardPage";
import { AuditLogPage } from "@/pages/settings/AuditLogPage";
import { GeneralSettingsPage } from "@/pages/settings/GeneralSettingsPage";
import { HealthSettingsPage } from "@/pages/settings/HealthSettingsPage";
import { IntegrationsSettingsPage } from "@/pages/settings/IntegrationsSettingsPage";
import { AdsolutIntegrationPage } from "@/pages/settings/AdsolutIntegrationPage";
import { AdsolutCoveragePage } from "@/pages/settings/AdsolutCoveragePage";
import { TelavoxIntegrationPage } from "@/pages/settings/TelavoxIntegrationPage";
import { ZammadIntegrationPage } from "@/pages/settings/ZammadIntegrationPage";
import { TrmmIntegrationPage } from "@/pages/settings/TrmmIntegrationPage";
import { M365IntegrationPage } from "@/pages/settings/M365IntegrationPage";
import { AssetsPage } from "@/pages/assets/AssetsPage";
import { OrdersPage } from "@/pages/orders/OrdersPage";
import { StatisticsPage } from "@/pages/statistics/StatisticsPage";
import { ContractsPage } from "@/pages/contracts/ContractsPage";
import { ContractArticlesPage } from "@/pages/contracts/ContractArticlesPage";
import { ContractsOverviewPage } from "@/pages/contracts/ContractsOverviewPage";
import { ContractM365Page } from "@/pages/contracts/ContractM365Page";
import { ContractM365CompanyPage } from "@/pages/contracts/ContractM365CompanyPage";
import { ZammadImportRunsListPage } from "@/pages/settings/zammad/ZammadImportRunsListPage";
import { ZammadImportRunDetailPage } from "@/pages/settings/zammad/ZammadImportRunDetailPage";
import { MailSettingsPage } from "@/pages/settings/MailSettingsPage";
import { MailDiagnosticsPage } from "@/pages/settings/MailDiagnosticsPage";
import { SlaSettingsPage } from "@/pages/settings/SlaSettingsPage";
import { IntakeFormsSettingsPage } from "@/pages/settings/IntakeFormsSettingsPage";
import { TemplatesSettingsPage } from "@/pages/settings/TemplatesSettingsPage";
import { PublicIntakeFormPage } from "@/pages/intake/PublicIntakeFormPage";
import { PublicSurveyPage } from "@/pages/surveys/PublicSurveyPage";
import { PublicKbArticlePage } from "@/pages/kb/PublicKbArticlePage";
import { SurveysSettingsPage } from "@/pages/settings/SurveysSettingsPage";
import { SurveyEditorPage } from "@/pages/settings/surveys/SurveyEditorPage";
import { SurveyResultsPage } from "@/pages/settings/surveys/SurveyResultsPage";
import { TicketsSettingsPage } from "@/pages/settings/TicketsSettingsPage";
import { TriggersSettingsPage } from "@/pages/settings/TriggersSettingsPage";
import { TriggerRunsPage } from "@/pages/settings/triggers/TriggerRunsPage";
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
import { KbHomePage } from "@/pages/kb/KbHomePage";
import { KbSectionPage } from "@/pages/kb/KbSectionPage";
import { KbArticlePage } from "@/pages/kb/KbArticlePage";
import { KbArticleEditPage } from "@/pages/kb/KbArticleEditPage";
import { KnowledgeBaseSettingsPage } from "@/pages/settings/KnowledgeBaseSettingsPage";
import { TimesheetPage } from "@/pages/timesheet/TimesheetPage";
import { TimesheetSettingsPage } from "@/pages/settings/TimesheetSettingsPage";
import { ActivityFeedPage } from "@/pages/activity/ActivityFeedPage";
import { SignaturesSettingsPage } from "@/pages/settings/SignaturesSettingsPage";

// The router reads the "current role" outside of React here (for the
// beforeLoad gate). The auth store is populated by bootstrapAuth() in
// main.tsx before the router mounts, so these reads always see real state.
// A session that still owes its TOTP challenge (amr "mfa-pending") is NOT
// treated as authenticated for app routes — the server rejects it anyway, so
// letting it paint the shell would just 403 every data call. Such a user is
// bounced to /login, where the page resumes the 2FA step.
function authedUser() {
  const { user } = authStore.get();
  if (!user || user.amr === "mfa-pending") return null;
  return user;
}

function authGate(allowed: readonly Role[]) {
  return ({ location }: { location: { pathname: string } }) => {
    const user = authedUser();
    if (user === null) {
      throw redirect({ to: "/login", search: { from: location.pathname } });
    }
    if (!allowed.includes(user.role)) {
      throw redirect({ to: "/" });
    }
  };
}

function anyAuthenticatedGate() {
  return ({ location }: { location: { pathname: string } }) => {
    if (!authedUser()) {
      throw redirect({ to: "/login", search: { from: location.pathname } });
    }
  };
}

const UNAUTHENTICATED_PATHS = new Set(["/login", "/setup"]);

/// Paths reachable without a session. Everything else — including unknown /
/// not-found paths that fall through to the root's notFoundComponent — requires
/// authentication, so an anonymous visitor bounces to /login instead of seeing
/// the app shell. The server-side authorization policies remain the actual
/// security boundary (every data endpoint returns 401); this just stops the
/// client from painting the shell + role chrome for a logged-out visitor.
function isPublicPath(path: string): boolean {
  if (UNAUTHENTICATED_PATHS.has(path)) return true;
  if (path.startsWith("/intake/")) return true;
  if (path.startsWith("/surveys/")) return true;
  if (path.startsWith("/kb/public/")) return true;
  return false;
}

/// Routes that render OUTSIDE AppShell — no sidebar, no CriticalBanner.
/// Used for the pop-out compose window so the agent can park it next to
/// the main tab with just the form visible. Public tokenised intake-form
/// fills also land here — the customer has no session and never sees the
/// agent UI.
function isBareRoute(path: string): boolean {
  if (UNAUTHENTICATED_PATHS.has(path)) return true;
  if (path.endsWith("/compose")) return true;
  if (path.startsWith("/intake/")) return true;
  if (path.startsWith("/surveys/")) return true;
  if (path.startsWith("/kb/public/")) return true;
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
    // Require a session for every non-public path. Without this, an unknown
    // path (e.g. /123) matches no child route and falls through to the
    // notFoundComponent below, which renders INSIDE the AppShell — exposing the
    // shell + role chrome to an anonymous visitor. Per-route gates only cover
    // declared routes; this closes the not-found gap. Public paths (login,
    // setup, tokenised intake/survey links) stay exempt.
    if (!authedUser() && !isPublicPath(path)) {
      throw redirect({ to: "/login", search: { from: path } });
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

// stubForPath was used by the v0.0.x KB placeholder before the real KB routes
// landed; kept around in case future placeholders need it. Inline if revived.

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

// v0.0.35 — Timesheet feature. Role gate keeps customers out; the actual
// "do they have the flag" check is in the sidebar visibility filter and
// inside the page itself for Tab 2/3. A direct /timesheet visit by an
// agent without the flag renders Tab 1 anyway — they just have no nav
// item to reach it. We do not redirect because admins audit-debugging
// "what does the page render for me without the flag" should work.
const timesheetRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/timesheet",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: TimesheetPage,
});

// Standalone Knowledge Base. Customers have no access in v0.0.31; the
// public-portal tier lands in v0.1.x with its own slug-based routing.
const kbRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/kb",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: KbHomePage,
});

const kbSectionRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/kb/sections/$sectionId",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: function KbSectionRoute() {
    const { sectionId } = kbSectionRoute.useParams();
    return <KbSectionPage sectionId={sectionId} />;
  },
});

const kbArticleRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/kb/articles/$articleId",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: function KbArticleRoute() {
    const { articleId } = kbArticleRoute.useParams();
    return <KbArticlePage articleId={articleId} />;
  },
});

const kbArticleEditRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/kb/articles/$articleId/edit",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: function KbArticleEditRoute() {
    const { articleId } = kbArticleEditRoute.useParams();
    return <KbArticleEditPage articleId={articleId} />;
  },
});

// New-article path uses ?sectionId=… so the editor pre-selects the
// originating section. URL-state is preferred over component-state so the
// page survives a refresh / shared link without losing context.
type KbNewArticleSearch = { sectionId?: string };
const kbArticleNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/kb/articles/new",
  beforeLoad: authGate(["Agent", "Admin"]),
  validateSearch: (raw: Record<string, unknown>): KbNewArticleSearch => ({
    sectionId: typeof raw.sectionId === "string" ? raw.sectionId : undefined,
  }),
  component: function KbNewArticleRoute() {
    const search = kbArticleNewRoute.useSearch();
    return <KbArticleEditPage articleId={null} initialSectionId={search.sectionId} />;
  },
});

const profileRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/profile",
  beforeLoad: anyAuthenticatedGate(),
  component: ProfilePage,
});

// v0.0.42 — Activity feed. Agent + Admin role gate; the
// activity_feed_enabled per-user flag is enforced server-side
// (endpoints return 403 when off, hub never enrolls the connection),
// so a manual /activity visit by a flag-off user shows the empty/error
// states from the page instead of 404. The sidebar entry hides itself
// when the flag is off so the user does not see a dead link.
const activityFeedRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/activity",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: ActivityFeedPage,
});

// v0.0.52 — Assets page (Tactical RMM mirror). Same gate pattern as
// Activity feed: Agent + Admin role gate at the route, per-user
// `assets_enabled` flag enforced both server-side (RequireAgent on
// /api/assets and the per-user flag on /auth/me) and via the sidebar
// hide so a flag-off user never sees a dead link.
const assetsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/assets",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: AssetsPage,
});

// v0.0.59 — Orders overview (Adsolut ERP mirror). Same gate pattern as
// Assets: Agent + Admin role gate at the route, per-user
// `adsolut_orders_enabled` flag enforced server-side (RequireAgent on
// /api/orders + the per-user flag on /auth/me) and via the sidebar hide.
const ordersRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/orders",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: OrdersPage,
});

// v0.0.12 stap 4 — history of @@-mentions received by the caller.
// Agent+Admin only; customers never receive mentions in this release.
// v0.0.69 — Statistics page (light tile builder). Same gate pattern as
// Assets/Orders: Agent + Admin role gate at the route, per-user
// `statistics_read` flag enforced server-side (RequireAgent + flag check on
// /api/statistics) and via the sidebar hide so a flag-off user never sees a
// dead link.
const statisticsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/statistics",
  beforeLoad: authGate(["Agent", "Admin"]),
  component: StatisticsPage,
});

// v0.0.76 — Contracts hub (tile launcher; modules land later). Agent + Admin
// role gate plus the per-user `contracts_enabled` flag, checked here as well
// as in the sidebar hide: the page has no backend surface yet, so the route
// gate is what keeps a flag-off user from deep-linking to it. The flag is
// server-sourced via /auth/me.
const contractsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/contracts",
  beforeLoad: (args) => {
    authGate(["Agent", "Admin"])(args);
    if (!authedUser()?.contractsEnabled) {
      throw redirect({ to: "/" });
    }
  },
  component: ContractsPage,
});

// Contract Articles — first live module behind the Contracts hub (Adsolut
// article-catalogue mirror). Same contracts_enabled gate as the hub; the
// /api/contracts/articles endpoints carry RequireAgent + an in-handler flag
// check, so this route gate is UI-side defence-in-depth.
const contractArticlesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/contracts/articles",
  beforeLoad: (args) => {
    authGate(["Agent", "Admin"])(args);
    if (!authedUser()?.contractsEnabled) {
      throw redirect({ to: "/" });
    }
  },
  component: ContractArticlesPage,
});

// Contracts overview — second live module behind the Contracts hub (Adsolut
// contracts mirror). Same contracts_enabled gate; the /api/contracts/overview
// endpoints carry RequireAgent + an in-handler flag check server-side.
const contractsOverviewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/contracts/overview",
  beforeLoad: (args) => {
    authGate(["Agent", "Admin"])(args);
    if (!authedUser()?.contractsEnabled) {
      throw redirect({ to: "/" });
    }
  },
  component: ContractsOverviewPage,
});

// Microsoft 365 matching — third live module behind the Contracts hub. Lists
// companies whose contracts reference admin-curated "M365-related" Adsolut
// articles. Same contracts_enabled gate; the /api/contracts/m365 endpoints
// carry RequireAgent + an in-handler flag check server-side.
const contractM365Route = createRoute({
  getParentRoute: () => rootRoute,
  path: "/contracts/m365-matching",
  beforeLoad: (args) => {
    authGate(["Agent", "Admin"])(args);
    if (!authedUser()?.contractsEnabled) {
      throw redirect({ to: "/" });
    }
  },
  component: ContractM365Page,
});

// One company's synced Microsoft 365 mailboxes (reached from the matching list).
// Same contracts_enabled gate as the list; the mailbox endpoint re-checks it.
const contractM365CompanyRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/contracts/m365-matching/$companyId",
  beforeLoad: (args) => {
    authGate(["Agent", "Admin"])(args);
    if (!authedUser()?.contractsEnabled) {
      throw redirect({ to: "/" });
    }
  },
  component: function ContractM365CompanyRoute() {
    const { companyId } = contractM365CompanyRoute.useParams();
    return <ContractM365CompanyPage companyId={companyId} />;
  },
});

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

const settingsTemplatesRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "templates",
  component: TemplatesSettingsPage,
});

const settingsSignaturesRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "signatures",
  component: SignaturesSettingsPage,
});

// v0.0.38 — survey designer + results
const settingsSurveysRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "surveys",
  component: SurveysSettingsPage,
});

const settingsSurveyEditorRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "surveys/$surveyId",
  component: function SettingsSurveyEditorRoute() {
    const { surveyId } = settingsSurveyEditorRoute.useParams();
    return <SurveyEditorPage surveyId={surveyId} />;
  },
});

const settingsSurveyNewRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "surveys-new",
  component: function SettingsSurveyNewRoute() {
    return <SurveyEditorPage surveyId={null} />;
  },
});

const settingsSurveyResultsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "surveys/$surveyId/results",
  component: function SettingsSurveyResultsRoute() {
    const { surveyId } = settingsSurveyResultsRoute.useParams();
    return <SurveyResultsPage surveyId={surveyId} />;
  },
});

const settingsTriggersRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "triggers",
  component: TriggersSettingsPage,
});

const settingsKnowledgeBaseRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "knowledge-base",
  component: KnowledgeBaseSettingsPage,
});

const settingsTimesheetRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "timesheet",
  component: TimesheetSettingsPage,
});

// Deep-link from global search and run-history "edit" button. Renders the
// list page with the editor pre-opened on the requested trigger.
const settingsTriggerDetailRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "triggers/$triggerId",
  component: function SettingsTriggerDetailRoute() {
    const { triggerId } = settingsTriggerDetailRoute.useParams();
    return <TriggersSettingsPage initialEditId={triggerId} />;
  },
});

const settingsTriggerRunsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "triggers/$triggerId/runs",
  component: function SettingsTriggerRunsRoute() {
    const { triggerId } = settingsTriggerRunsRoute.useParams();
    return <TriggerRunsPage triggerId={triggerId} />;
  },
});

const settingsIntegrationsRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations",
  component: IntegrationsSettingsPage,
});

const settingsAdsolutIntegrationRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations/adsolut",
  component: AdsolutIntegrationPage,
});

// v0.0.34 — Telavox call-popup integration. Admin-only; route sits next to
// /settings/integrations/adsolut so both detail pages share the same
// breadcrumb back to /settings/integrations.
const settingsTelavoxIntegrationRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations/telavox",
  component: TelavoxIntegrationPage,
});

// v0.0.41 — Zammad migration link. Admin-only; phase 1 only ships
// connectivity (base URL, token, test connection) — ticket picker,
// dry-run and import surface here in later phases.
const settingsZammadIntegrationRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations/zammad",
  component: ZammadIntegrationPage,
});

// v0.0.52 — Tactical RMM integration. Admin-only. Houses base URL,
// API key, enable toggle, sync interval, manual sync trigger, client
// mappings UI and the integration_audit log reader.
const settingsTrmmIntegrationRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations/trmm",
  component: TrmmIntegrationPage,
});

// v0.0.77 — Microsoft 365 customer-tenant reader. Admin-only. Houses the
// shared multi-tenant app credentials (tenant id, client id, client secret),
// the required Graph Application permissions + redirect URI an admin must
// register in Entra ID, a credential test, and the integration_audit reader.
const settingsM365IntegrationRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations/m365",
  component: M365IntegrationPage,
});

// v0.0.41 phase 3 — dry-run engine pages. Runs-list lives on its own
// URL so admins can deep-link to past runs from a separate tab; the
// detail page renders the per-ticket mapping verdict for one run.
const settingsZammadRunsListRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations/zammad/runs",
  component: ZammadImportRunsListPage,
});
const settingsZammadRunDetailRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations/zammad/runs/$runId",
  component: ZammadImportRunDetailPage,
});

// v0.0.30 — coverage overview page. URL-state filter (tab + bucket) keeps
// deep-links from the tile + back/forward navigation honest. All four
// keys are optional; the route component derives sensible defaults.
type AdsolutCoverageSearch = {
  tab?: "companies" | "contacts";
  bucket?: string;
  search?: string;
  page?: number;
};
const settingsAdsolutCoverageRoute = createRoute({
  getParentRoute: () => settingsRoute,
  path: "integrations/adsolut/coverage",
  validateSearch: (raw: Record<string, unknown>): AdsolutCoverageSearch => ({
    tab: raw.tab === "contacts" ? "contacts" : raw.tab === "companies" ? "companies" : undefined,
    bucket: typeof raw.bucket === "string" ? raw.bucket : undefined,
    search: typeof raw.search === "string" ? raw.search : undefined,
    page: typeof raw.page === "string"
      ? Number(raw.page)
      : (raw.page as number | undefined),
  }),
  component: AdsolutCoveragePage,
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

// v0.0.38 — public survey link. Same bare-shell semantics as intake.
const publicSurveyRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/surveys/$token",
  component: function PublicSurveyRoute() {
    const { token } = publicSurveyRoute.useParams();
    return <PublicSurveyPage token={token} />;
  },
});

// v0.0.75 — public KB article reader. Same bare-shell semantics; the
// server only serves Published articles (and only while the admin toggle
// is on), so no beforeLoad gate is needed here.
const publicKbArticleRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/kb/public/$articleId",
  component: function PublicKbArticleRoute() {
    const { articleId } = publicKbArticleRoute.useParams();
    return <PublicKbArticlePage articleId={articleId} />;
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
  timesheetRoute,
  kbRoute,
  kbSectionRoute,
  kbArticleNewRoute,
  kbArticleRoute,
  kbArticleEditRoute,
  profileRoute,
  profileMentionsRoute,
  activityFeedRoute,
  assetsRoute,
  ordersRoute,
  statisticsRoute,
  contractsRoute,
  contractArticlesRoute,
  contractsOverviewRoute,
  contractM365Route,
  contractM365CompanyRoute,
  settingsRoute.addChildren([
    settingsIndexRoute,
    settingsGeneralRoute,
    settingsMailRoute,
    settingsSlaRoute,
    settingsIntakeFormsRoute,
    settingsTemplatesRoute,
    settingsSignaturesRoute,
    settingsSurveysRoute,
    settingsSurveyNewRoute,
    settingsSurveyEditorRoute,
    settingsSurveyResultsRoute,
    settingsTriggersRoute,
    settingsTriggerDetailRoute,
    settingsTriggerRunsRoute,
    settingsKnowledgeBaseRoute,
    settingsTimesheetRoute,
    settingsIntegrationsRoute,
    settingsAdsolutIntegrationRoute,
    settingsAdsolutCoverageRoute,
    settingsTelavoxIntegrationRoute,
    settingsZammadIntegrationRoute,
    settingsZammadRunsListRoute,
    settingsZammadRunDetailRoute,
    settingsTrmmIntegrationRoute,
    settingsM365IntegrationRoute,
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
  publicSurveyRoute,
  publicKbArticleRoute,
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
