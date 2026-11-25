import { request } from "@/lib/http";
import type { UiTheme } from "@/lib/theme";
import type { Role } from "@/lib/roles";

export type AuthUserPayload = {
  id: string;
  email: string;
  role: Role;
  amr: string;
  twoFactorEnabled: boolean;
  timesheetEnabled: boolean;
  timesheetManager: boolean;
  isIsoMgm: boolean;
  isIsoDpo: boolean;
  kbEnabled: boolean;
  searchEnabled: boolean;
  activityFeedEnabled: boolean;
  assetsEnabled: boolean;
  adsolutTimesheetEnabled: boolean;
  timesheetBackofficeEnabled: boolean;
  backofficeFormsEnabled: boolean;
  // v0.1.59 — Order Management (TD SYNNEX orders); server-enforced flag.
  orderManagementEnabled: boolean;
  calendarPlanningEnabled: boolean;
  // v0.1.67 — Campaigns (bulk mail); server-enforced flag.
  campaignsEnabled: boolean;
  adsolutOrdersEnabled: boolean;
  statisticsRead: boolean;
  statisticsWrite: boolean;
  contractsEnabled: boolean;
  insightsEnabled: boolean;
  feedbackEnabled: boolean;
  feedbackOwnOnly: boolean;
  adsolutConnected: boolean;
  dashboardTiles: string[];
  // v0.0.44 — server-resolved theme (user pref → admin default → factory
  // 'steaan' since v0.0.108). ThemeProvider uses this as the source of truth
  // on bootstrap.
  effectiveTheme: UiTheme;
  slaEnabled?: boolean;
};
export type MeResponse = {
  user: AuthUserPayload | null;
  serverTimeUtc: string;
};
export type SetupStatus = { available: boolean };
export type LoginResponse = {
  email: string;
  role: Role;
  twoFactorRequired: boolean;
  /// v0.1.3 — Security.TwoFactor.Required is on and this local account has
  /// no authenticator yet: the session is pending until enrollment finishes.
  enrollmentRequired: boolean;
};
export type TotpEnrollment = {
  secret: string;
  otpauthUri: string;
};
export type RecoveryCodesResponse = { recoveryCodes: string[] };
export type AuthConfig = {
  microsoftEnabled: boolean;
  setupAvailable: boolean;
  /// v0.1.0 — the customer portal is switched on (staff login shows a link).
  portalEnabled?: boolean;
};
export const authApi = {
  setupStatus: () => request<SetupStatus>("GET", "/api/auth/setup/status"),
  config: () => request<AuthConfig>("GET", "/api/auth/config"),
  createAdmin: (email: string, password: string) =>
    request<{ email: string; role: Role }>("POST", "/api/auth/setup/create-admin", { email, password }),
  login: (email: string, password: string) =>
    request<LoginResponse>("POST", "/api/auth/login", { email, password }),
  verifyTwoFactor: (code: string) =>
    request<void>("POST", "/api/auth/2fa/verify", { code }),
  logout: () => request<void>("POST", "/api/auth/logout"),
  me: () => request<MeResponse>("GET", "/api/auth/me"),
  beginTotpEnroll: () => request<TotpEnrollment>("POST", "/api/auth/2fa/enroll/begin"),
  confirmTotpEnroll: (code: string) =>
    request<RecoveryCodesResponse>("POST", "/api/auth/2fa/enroll/confirm", { code }),
  // v0.1.3 — disabling 2FA is a step-up action: it requires a live
  // authenticator code (or a recovery code), not just a session.
  disableTotp: (code: string) => request<void>("POST", "/api/auth/2fa/disable", { code }),
  // v0.1.3 — self-service password change for Local staff accounts. Revokes
  // every other session on success.
  changePassword: (currentPassword: string, newPassword: string) =>
    request<void>("POST", "/api/auth/change-password", { currentPassword, newPassword }),
  /// "My sessions": the caller's open sign-ins, addressed by a public handle
  /// (never the session token). Revoking signs that device out.
  sessions: () => request<{ items: MySession[] }>("GET", "/api/auth/sessions"),
  revokeSession: (publicId: string) =>
    request<void>("DELETE", `/api/auth/sessions/${encodeURIComponent(publicId)}`),
  revokeOtherSessions: () => request<void>("POST", "/api/auth/sessions/revoke-others"),
};
export type MySession = {
  publicId: string;
  createdUtc: string;
  lastSeenUtc: string;
  expiresUtc: string;
  ip: string | null;
  userAgent: string | null;
  amr: string;
  current: boolean;
};
