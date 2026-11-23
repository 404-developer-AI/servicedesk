import { request } from "@/lib/http";

// ---- Settings ----

export type SettingEntry = {
  key: string;
  value: string;
  valueType: string;
  category: string;
  description: string;
  defaultValue: string;
  updatedUtc: string;
};
export type NavigationSettings = {
  showOpenTickets: boolean;
};
export type NotificationsSettings = {
  popupDurationSeconds: number;
};
export type MailComposeSettings = {
  /// When true, the composer warns before sending if the typed message mentions
  /// an attachment but none is attached.
  forgottenAttachmentEnabled: boolean;
  /// Comma-separated EN+NL keywords that trigger the warning.
  forgottenAttachmentKeywords: string;
};
/// v0.0.102 — agent-readable bulk-action knobs (Settings → Tickets → General).
export type BulkActionsSettings = {
  /// When false the ticket list hides row checkboxes and the bulk bar.
  enabled: boolean;
  /// Server-enforced cap on tickets per bulk action.
  maxSelection: number;
};
export type TicketGroupingSettings = {
  /// When true and the ticket list is grouped by Status, each group is sorted by
  /// its semantic state category instead of the view's global sort.
  enabled: boolean;
  /// Sort field/direction applied inside Pending-state status groups.
  pendingField: string;
  pendingDirection: "asc" | "desc";
  /// Sort field/direction applied inside Open- or New-state status groups.
  openNewField: string;
  openNewDirection: "asc" | "desc";
};
export type CopilotSettings = {
  /// Master switch — when false the nav launcher button is hidden.
  enabled: boolean;
  /// URL opened by the launcher (the real Microsoft Copilot).
  url: string;
  /// Label shown on the nav button.
  label: string;
  /// true = focused side-panel popup window; false = a normal new tab.
  openInPopup: boolean;
};
export const settingsApi = {
  list: (category?: string) => {
    const params = category ? `?category=${encodeURIComponent(category)}` : "";
    return request<SettingEntry[]>("GET", `/api/settings${params}`);
  },
  update: (key: string, value: string) =>
    request<void>("PUT", `/api/settings/${encodeURIComponent(key)}`, { value }),
  navigation: () =>
    request<NavigationSettings>("GET", "/api/settings/navigation"),
  /// Mobile web: whether touch devices are sent to the /m views.
  mobile: () => request<{ autoRedirect: boolean }>("GET", "/api/settings/mobile"),
  notifications: () =>
    request<NotificationsSettings>("GET", "/api/settings/notifications"),
  mailCompose: () =>
    request<MailComposeSettings>("GET", "/api/settings/mail-compose"),
  ticketGrouping: () =>
    request<TicketGroupingSettings>("GET", "/api/settings/ticket-grouping"),
  bulkActions: () =>
    request<BulkActionsSettings>("GET", "/api/settings/bulk-actions"),
  copilot: () =>
    request<CopilotSettings>("GET", "/api/settings/copilot"),
  ticketWarnings: () =>
    request<{ showContactNotLinked: boolean }>("GET", "/api/settings/ticket-warnings"),
};
