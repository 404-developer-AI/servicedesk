import { request } from "@/lib/http";

// ---- Admin user-management (v0.0.13 step 3) --------------------------

export type UserAdminRow = {
  id: string;
  email: string;
  role: "Agent" | "Admin" | "Customer";
  authMode: "Local" | "Microsoft";
  externalSubject: string | null;
  isActive: boolean;
  twoFactorEnabled: boolean;
  createdUtc: string;
  lastLoginUtc: string | null;
  timesheetEnabled: boolean;
  timesheetManager: boolean;
  // v0.0.40 — per-user ISO 27001 workflow flags. Same toggle-pattern
  // as the timesheet flags; default false on insert.
  isIsoMgm: boolean;
  isIsoDpo: boolean;
  // v0.0.40 polish — Knowledge Base opt-in. False on insert; admins flip
  // it on for the agents who should see the KB sidebar entry.
  kbEnabled: boolean;
  // Sidebar feature flag. Default true on insert so brownfield
  // Agent/Admin users keep their search bar on upgrade.
  searchEnabled: boolean;
  // v0.0.42 — per-user opt-in for the agent activity feed. Default
  // false on insert; admins flip it on for the users who should see
  // the dashboard tile + /activity admin page.
  activityFeedEnabled: boolean;
  // v0.0.52 — per-user opt-in for the Assets page (Tactical RMM
  // mirror). Backfilled to true for Agent + Admin on first upgrade;
  // false on new insert until an admin flips it.
  assetsEnabled: boolean;
  // Per-user opt-in for the Adsolut timesheet tab. False on insert;
  // the tab also requires the Adsolut integration to be connected.
  adsolutTimesheetEnabled: boolean;
  // v0.0.56 — per-user opt-in for the back-office Resolved + CWI tabs.
  timesheetBackofficeEnabled: boolean;
  // Per-user opt-in for the back-office forms feature.
  backofficeFormsEnabled: boolean;
  // v0.1.59 — per-user opt-in for Order Management (TD SYNNEX orders).
  orderManagementEnabled: boolean;
  // v0.1.61 — per-user "Plan in calendars" opt-in + the Outlook calendar
  // that receives the user's planned appointments (null = default calendar).
  calendarPlanningEnabled: boolean;
  calendarId: string | null;
  calendarName: string | null;
  // v0.1.67 — per-user opt-in for Campaigns (bulk mail).
  campaignsEnabled: boolean;
  // v0.0.59 — per-user opt-in for the Adsolut Orders feature.
  adsolutOrdersEnabled: boolean;
  // v0.0.69 — per-user opt-in for the Statistics feature. read = view
  // assigned tiles; write = build + assign tiles. Independent flags.
  statisticsRead: boolean;
  statisticsWrite: boolean;
  // v0.0.76 — per-user opt-in for the Contracts page (tile hub).
  contractsEnabled: boolean;
  // v0.1.13 — per-user opt-in for the Insights reporting dashboard.
  insightsEnabled: boolean;
  // Per-user opt-in for the Employee Feedback feature (full access).
  feedbackEnabled: boolean;
  // v0.0.90 — restricted Employee Feedback access (log + see own only).
  feedbackOwnOnly: boolean;
  // Per-user Dashboard tile preferences. Empty array on insert
  // (admins opt the user in to specific tiles).
  dashboardTiles: string[];
};

export type FeatureFlagsUpdate = Partial<{
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
  adsolutOrdersEnabled: boolean;
  statisticsRead: boolean;
  statisticsWrite: boolean;
  contractsEnabled: boolean;
  insightsEnabled: boolean;
  feedbackEnabled: boolean;
  feedbackOwnOnly: boolean;
  orderManagementEnabled: boolean;
  calendarPlanningEnabled: boolean;
  campaignsEnabled: boolean;
  /// Empty string clears (default calendar).
  calendarId: string;
  calendarName: string;
}>;

export type UserCalendarOption = {
  id: string;
  name: string;
  isDefault: boolean;
  canEdit: boolean;
};

export type UserCalendarsResponse = {
  items: UserCalendarOption[];
  error?: string;
};

export type M365PickerUser = {
  oid: string;
  displayName: string | null;
  userPrincipalName: string | null;
  mail: string | null;
  accountEnabled: boolean;
};

export const adminUserApi = {
  list: () => request<UserAdminRow[]>("GET", "/api/admin/users/"),

  /// Graph typeahead. 409 if M365 login is disabled or Graph is not
  /// fully configured — the UI surfaces the server's message verbatim.
  searchM365: (q: string, limit = 20) => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    params.set("limit", String(limit));
    return request<M365PickerUser[]>("GET", `/api/admin/users/m365/search?${params.toString()}`);
  },

  addFromM365: (oid: string, role: "Agent" | "Admin") =>
    request<UserAdminRow>("POST", "/api/admin/users/m365", { oid, role }),

  addLocal: (email: string, password: string, role: "Agent" | "Admin") =>
    request<UserAdminRow>("POST", "/api/admin/users/local", { email, password, role }),

  upgradeToM365: (userId: string, oid: string) =>
    request<UserAdminRow>("POST", `/api/admin/users/${userId}/upgrade-to-m365`, { oid }),

  updateRole: (userId: string, role: "Agent" | "Admin") =>
    request<UserAdminRow>("PUT", `/api/admin/users/${userId}/role`, { role }),

  activate: (userId: string) =>
    request<UserAdminRow>("POST", `/api/admin/users/${userId}/activate`, {}),

  deactivate: (userId: string) =>
    request<UserAdminRow>("POST", `/api/admin/users/${userId}/deactivate`, {}),

  remove: (userId: string) =>
    request<void>("DELETE", `/api/admin/users/${userId}`),

  // v0.1.3 — admin-initiated password reset for Local staff accounts.
  // Revokes every open session of the target.
  resetPassword: (userId: string, newPassword: string) =>
    request<void>("POST", `/api/admin/users/${userId}/reset-password`, { newPassword }),

  setTimesheetFlags: (
    userId: string,
    enabled: boolean,
    manager: boolean,
  ) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/timesheet-flags`,
      { enabled, manager },
    ),

  // v0.0.40 — ISO 27001 per-user role flags.
  setIsoFlags: (userId: string, mgm: boolean, dpo: boolean) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/iso-flags`,
      { mgm, dpo },
    ),

  // v0.0.40 polish — Knowledge Base per-user opt-in.
  setKbFlag: (userId: string, enabled: boolean) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/kb-flag`,
      { enabled },
    ),

  // Consolidated partial-update across every per-user feature flag.
  // Only the fields present in `update` are written; the rest stay as
  // they are. One transaction server-side, one audit row per call.
  setFeatureFlags: (userId: string, update: FeatureFlagsUpdate) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/feature-flags`,
      update,
    ),

  /// Outlook calendars of one user (Graph). On a Graph failure the server
  /// returns an empty list plus an `error` text.
  listCalendars: (userId: string) =>
    request<UserCalendarsResponse>("GET", `/api/admin/users/${userId}/calendars`),

  // Replace the full set of enabled Dashboard tile-ids for one user.
  // Unknown ids cause a 400; sending an empty array clears the set.
  setDashboardTiles: (userId: string, tileIds: string[]) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/dashboard-tiles`,
      { tileIds },
    ),

  /// v0.0.35-E — per-user Timesheet preference overrides. Reads + writes
  /// the four nullable columns on `users` (day start, day target, week
  /// target, work-days). Each field is independently nullable; a null
  /// means "fall back to the global default".
  getTimesheetPreferences: (userId: string) =>
    request<TimesheetUserPreferencesResponse>(
      "GET",
      `/api/admin/users/${userId}/timesheet-preferences`,
    ),

  setTimesheetPreferences: (
    userId: string,
    body: TimesheetOverrideBody,
  ) =>
    request<TimesheetOverridePayload>(
      "PUT",
      `/api/admin/users/${userId}/timesheet-preferences`,
      body,
    ),
};

export type TimesheetOverridePayload = {
  dayStartMinutes: number | null;
  targetMinutesPerDay: number | null;
  targetMinutesPerWeek: number | null;
  workDays: number[] | null;
  /// v0.0.36 — daily ceiling on absence-task minutes before the ISO-week
  /// is flagged 'target not met' on Tab 3. null = fall back to the global
  /// default (Settings → Timesheet → Max absence per day).
  maxAbsenceMinutesPerDay: number | null;
  /// v0.0.36 — office-hour window used by Tab 1 to flag row-to-row gaps
  /// and overlaps. null = fall back to global.
  officeStartMinutes: number | null;
  officeEndMinutes: number | null;
};

export type TimesheetUserPreferencesResponse = {
  effective: {
    dayStartMinutes: number;
    targetMinutesPerDay: number;
    targetMinutesPerWeek: number;
    workDays: number[];
    maxAbsenceMinutesPerDay: number;
    officeStartMinutes: number;
    officeEndMinutes: number;
  };
  override: TimesheetOverridePayload;
};

export type TimesheetOverrideBody = TimesheetOverridePayload;
