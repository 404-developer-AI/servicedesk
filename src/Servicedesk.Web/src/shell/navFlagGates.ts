import type { AuthUser } from "@/auth/authStore";

/// Per-user feature flags that hide a nav entry (role gates live on the
/// NavItem itself). Visibility only — every route and API behind these flags
/// enforces the same flag server-side (CLAUDE.md → a per-user feature flag is
/// an access boundary).
export const NAV_FLAG_GATES: Record<string, (user: AuthUser | null | undefined) => boolean> = {
  // v0.0.35 — Timesheet: own registration or the manager overview.
  "/timesheet": (u) => !!u?.timesheetEnabled || !!u?.timesheetManager,
  // v0.0.40 — Knowledge Base.
  "/kb": (u) => !!u?.kbEnabled,
  // v0.0.42 — Activity feed (the SignalR hub enforces it too).
  "/activity": (u) => !!u?.activityFeedEnabled,
  // v0.0.52 — Assets (Tactical RMM mirror).
  "/assets": (u) => !!u?.assetsEnabled,
  // v0.0.59 — Orders (Adsolut ERP mirror).
  "/orders": (u) => !!u?.adsolutOrdersEnabled,
  // v0.0.69 — Statistics (statistics_read).
  "/statistics": (u) => !!u?.statisticsRead,
  // v0.1.13 — Insights.
  "/insights": (u) => !!u?.insightsEnabled,
  // v0.1.54 — Back office forms.
  "/backoffice-forms": (u) => !!u?.backofficeFormsEnabled,
  // v0.1.59 — Order Management (TD SYNNEX).
  "/order-management": (u) => !!u?.orderManagementEnabled,
  // v0.1.67 — Campaigns (bulk mail).
  "/campaigns": (u) => !!u?.campaignsEnabled,
  // v0.1.67 (phase 3) — the Maintenance planner shares the Campaigns flag.
  "/maintenance": (u) => !!u?.campaignsEnabled,
  // v0.1.25 — SLA switched off globally (Settings → SLA): no SLA log.
  "/sla-log": (u) => u?.slaEnabled !== false,
  // v0.0.76 — Contracts.
  "/contracts": (u) => !!u?.contractsEnabled,
  // Employee Feedback — full (feedback_enabled) or own-only access.
  "/feedback": (u) => !!u?.feedbackEnabled || !!u?.feedbackOwnOnly,
};

/// True when no flag gate hides the nav entry for this user.
export function passesNavFlagGate(path: string, user: AuthUser | null | undefined): boolean {
  const gate = NAV_FLAG_GATES[path];
  return gate ? gate(user) : true;
}
