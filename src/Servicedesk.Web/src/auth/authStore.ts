import { useSyncExternalStore } from "react";
import type { Role } from "@/lib/roles";

export type AuthUser = {
  id: string;
  email: string;
  role: Role;
  amr: string;
  twoFactorEnabled: boolean;
  /// v0.0.35 — per-user Timesheet feature flags. Surface on /auth/me so
  /// the sidebar can hide the Timesheet nav item without an extra
  /// round-trip on every page load.
  timesheetEnabled: boolean;
  timesheetManager: boolean;
  /// v0.0.40 — per-user ISO 27001 workflow flags. Surface on /auth/me so
  /// the ticket-detail page can conditionally render the classification
  /// buttons without a second round-trip.
  isIsoMgm: boolean;
  isIsoDpo: boolean;
  /// v0.0.40 polish — KB access is now opt-in per user. Sidebar +
  /// Settings nav hide the entries when this is false.
  kbEnabled: boolean;
  /// Per-user Sidebar feature flag for the global search bar.
  /// Default true server-side so brownfield users keep the bar.
  searchEnabled: boolean;
  /// v0.0.42 — per-user opt-in for the agent activity feed.
  /// Gates the dashboard tile, the /activity page nav entry,
  /// and the SignalR hub's group enrollment.
  activityFeedEnabled: boolean;
  /// v0.0.52 — per-user opt-in for the Assets page (Tactical RMM
  /// mirror). Backfilled to true for Agent + Admin on first upgrade.
  assetsEnabled: boolean;
  /// Per-user opt-in for the Adsolut timesheet tab. Gates the 4th
  /// Timesheet tab, but only in combination with `adsolutConnected` —
  /// the flag on its own surfaces nothing.
  adsolutTimesheetEnabled: boolean;
  /// v0.0.56 — per-user opt-in for the back-office Resolved + CWI
  /// timesheet tabs. Gates the two tabs in the Timesheet page.
  timesheetBackofficeEnabled: boolean;
  /// v0.0.59 — per-user opt-in for the Adsolut Orders feature (navbar
  /// overview under Assets, order detail, the ticket "Sync orders" button
  /// and "::" order linking). Surfaces nothing on its own without the
  /// Adsolut integration being connected.
  adsolutOrdersEnabled: boolean;
  /// v0.0.69 — per-user opt-in for the Statistics feature. `read` gates the
  /// Statistics page + the tiles assigned to the user; `write` gates the
  /// tile-builder (creating + assigning tiles). The two are independent.
  statisticsRead: boolean;
  statisticsWrite: boolean;
  /// v0.0.76 — per-user opt-in for the Contracts page (tile hub; the
  /// contract data model lands later). Gates the sidebar nav entry and
  /// the /contracts route.
  contractsEnabled: boolean;
  /// Per-user opt-in for the Employee Feedback feature — FULL access (shared
  /// board). Gates the sidebar nav entry and the /feedback route.
  feedbackEnabled: boolean;
  /// v0.0.90 — RESTRICTED Employee Feedback access: may log feedback (manual +
  /// from ticket activity) but only see/edit own rows; management fields are
  /// read-only. Also opens the nav/route (in own-only mode). Ignored when
  /// feedbackEnabled is true (full access wins).
  feedbackOwnOnly: boolean;
  /// Whether the Adsolut integration is currently connected (server-
  /// resolved at /auth/me time). Tenant-global rather than per-user, but
  /// surfaced here so the Adsolut timesheet tab can gate without the
  /// admin-only integrations status endpoint.
  adsolutConnected: boolean;
  /// Per-user Dashboard tile preferences. Ordered list of
  /// {tileId, size} pairs; tiles whose id is not in this list are
  /// hidden on the Dashboard page. Size cycles small/medium/wide/full
  /// via the edit-mode UI; default empty (no tiles) on first upgrade.
  dashboardTiles: { tileId: string; size: DashboardTileSize }[];
  /// v0.0.44 — Server-resolved effective theme (user preference falling
  /// back to admin default, then to 'light'). The ThemeProvider syncs
  /// from this on bootstrap so the first authenticated paint matches
  /// the saved choice across devices.
  effectiveTheme: "light" | "dark";
};

export type DashboardTileSize = "small" | "medium" | "wide" | "full";

export type AuthState = {
  status: "loading" | "ready";
  user: AuthUser | null;
  setupAvailable: boolean;
};

let state: AuthState = {
  status: "loading",
  user: null,
  setupAvailable: false,
};

const listeners = new Set<() => void>();

function emit() {
  listeners.forEach((l) => l());
}

export const authStore = {
  get: () => state,
  set(next: AuthState) {
    state = next;
    emit();
  },
  patch(partial: Partial<AuthState>) {
    state = { ...state, ...partial };
    emit();
  },
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
};

export function useAuth(): AuthState {
  return useSyncExternalStore(authStore.subscribe, authStore.get, authStore.get);
}
