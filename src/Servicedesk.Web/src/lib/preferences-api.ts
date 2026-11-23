import { request } from "@/lib/http";
import type { UiTheme } from "@/lib/theme";

// ---- View Access Admin (direct assignment) ----

// ---- User Preferences ----

export type ColumnPreference = {
  columns: string;
  source: "user-view" | "view" | "user" | "default";
  /// v0.1.18 — the view locks its column layout (no column picker).
  locked: boolean;
};
export type WorkspaceEntryDto = { key: string; value: string };
export const preferencesApi = {
  getColumns: (viewId?: string) => {
    const qs = viewId ? `?viewId=${viewId}` : "";
    return request<ColumnPreference>("GET", `/api/preferences/columns${qs}`);
  },
  saveColumns: (columns: string, viewId?: string) => {
    const qs = viewId ? `?viewId=${viewId}` : "";
    return request<void>("PUT", `/api/preferences/columns${qs}`, { columns });
  },
  resetColumns: (viewId?: string) => {
    const qs = viewId ? `?viewId=${viewId}` : "";
    return request<void>("DELETE", `/api/preferences/columns${qs}`);
  },
  getWorkspace: () =>
    request<Record<string, string>>("GET", "/api/preferences/workspace"),
  saveWorkspace: (entries: WorkspaceEntryDto[]) =>
    request<void>("PUT", "/api/preferences/workspace", { entries }),
  deleteWorkspaceKey: (key: string) =>
    request<void>(
      "DELETE",
      `/api/preferences/workspace/${encodeURIComponent(key)}`,
    ),
  /** Fire-and-forget save using keepalive — survives page unload. */
  fireAndForgetWorkspaceSave: (entries: WorkspaceEntryDto[]) => {
    void request("PUT", "/api/preferences/workspace", { entries }, { keepalive: true }).catch(() => {});
  },
  // v0.0.44 — user-level theme override (steaan | light | dark since
  // v0.0.108). Source = "user" when the value comes from the
  // user_preferences row, "default" when it falls back to the admin-wide
  // Ui.DefaultTheme setting.
  getUiTheme: () =>
    request<{ theme: UiTheme; source: "user" | "default" }>(
      "GET",
      "/api/preferences/ui-theme",
    ),
  setUiTheme: (theme: UiTheme) =>
    request<void>("PUT", "/api/preferences/ui-theme", { theme }),
  resetUiTheme: () => request<void>("DELETE", "/api/preferences/ui-theme"),
  // v0.0.60 — feature pages the user pinned out of the sidebar "Features"
  // flyout so they render inline. Persisted per-user (survives logout/login).
  getPinnedFeatures: () =>
    request<PinnedFeaturesPreference>("GET", "/api/preferences/pinned-features"),
  setPinnedFeatures: (paths: string[]) =>
    request<PinnedFeaturesPreference>("PUT", "/api/preferences/pinned-features", { paths }),
  // v0.0.65 — saved views the user pinned out of the sidebar "Views" flyout so
  // they render inline. Persisted per-user (survives logout/login).
  getPinnedViews: () =>
    request<PinnedViewsPreference>("GET", "/api/preferences/pinned-views"),
  setPinnedViews: (ids: string[]) =>
    request<PinnedViewsPreference>("PUT", "/api/preferences/pinned-views", { ids }),
  // v0.0.78 — where the sidebar "remove from Recent" (×) action sends the
  // agent when they dismiss the ticket they currently have open. Token is
  // "dashboard", "timesheet", or "view:<guid>".
  getPostCloseRedirect: () =>
    request<PostCloseRedirectPreference>("GET", "/api/preferences/post-close-redirect"),
  setPostCloseRedirect: (target: string) =>
    request<PostCloseRedirectPreference>("PUT", "/api/preferences/post-close-redirect", { target }),
  /// v0.1.54 — which notifications also arrive by mail (the bell entry
  /// always comes). Defaults: tags on, replies on, likes off.
  getNotificationMail: () =>
    request<NotificationMailPreferences>("GET", "/api/preferences/notification-mail"),
  setNotificationMail: (change: Partial<NotificationMailPreferences>) =>
    request<void>("PUT", "/api/preferences/notification-mail", change),
};
export type NotificationMailPreferences = { tags: boolean; replies: boolean; likes: boolean };
export type PinnedFeaturesPreference = { paths: string[] };
export type PinnedViewsPreference = { ids: string[] };
export type PostCloseRedirectPreference = { target: string };
