import type { View } from "@/lib/ticket-api";

/** Concrete TanStack navigation target for the post-close redirect. */
export type PostCloseTarget =
  | { to: "/" }
  | { to: "/timesheet" }
  | { to: "/tickets"; search: { viewId: string } };

/** Stable value used by the Profile dropdown for "go to the dashboard". */
export const DASHBOARD_TOKEN = "dashboard";
/** Stable value used by the Profile dropdown for "go to the timesheet". */
export const TIMESHEET_TOKEN = "timesheet";
/** Builds the dropdown/preference token for a saved view. */
export const viewToken = (viewId: string) => `view:${viewId}`;

/**
 * Resolve the stored post-close redirect token to a concrete navigation
 * target, re-validating against what the agent can currently reach. A
 * "view:<id>" token whose view is no longer accessible, or a "timesheet"
 * token after the per-user flag was revoked, both fall back to the dashboard
 * so the sidebar's × action never strands the agent on a route they cannot
 * open.
 */
export function resolvePostCloseTarget(
  token: string | undefined,
  accessibleViews: readonly View[],
  canTimesheet: boolean,
): PostCloseTarget {
  if (token === TIMESHEET_TOKEN) {
    return canTimesheet ? { to: "/timesheet" } : { to: "/" };
  }
  if (token?.startsWith("view:")) {
    const viewId = token.slice("view:".length);
    if (accessibleViews.some((v) => v.id === viewId)) {
      return { to: "/tickets", search: { viewId } };
    }
    return { to: "/" };
  }
  // "dashboard" — and any unknown/stale token — lands on the dashboard.
  return { to: "/" };
}
