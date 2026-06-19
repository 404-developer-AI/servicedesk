import { Outlet } from "@tanstack/react-router";
import { Toaster } from "sonner";
import { useQuery } from "@tanstack/react-query";
import { Sidebar } from "@/shell/Sidebar";
import { CriticalBanner } from "@/components/health/CriticalBanner";
import { MaintenanceBanner } from "@/components/maintenance/MaintenanceBanner";
import { IncomingCallPopup } from "@/components/integrations/IncomingCallPopup";
import { useSecondarySidebarStore } from "@/stores/useSecondarySidebarStore";
import { usePresenceConnection } from "@/hooks/usePresence";
import { useNotificationSignalR } from "@/hooks/useNotificationSignalR";
import { useTimesheetCommentsSignalR } from "@/hooks/useTimesheetCommentsSignalR";
import { useIntegrationsSignalR } from "@/hooks/useIntegrationsSignalR";
import { useTelavoxCallStream } from "@/hooks/useTelavoxCallStream";
import { useWorkspaceAutoSave } from "@/hooks/useWorkspaceAutoSave";
import { settingsApi } from "@/lib/api";
import { useAuth } from "@/auth/authStore";
import { OrderPillHost } from "@/pages/orders/OrderPillHost";

export function AppShell() {
  usePresenceConnection();
  // Hoisted to shell-level so the IntegrationsHub handlers stay registered
  // for the entire authenticated session — page-level callers would tear
  // down their handlers on navigation, leaving the singleton connection
  // open with zero handlers and producing "no client method found"
  // warnings every time the server pushed a sync-tick or status flip.
  useIntegrationsSignalR();
  // v0.0.34 — Telavox call-popup stream. Internally role-gates so the
  // WebSocket only opens for Agent/Admin sessions; the hub itself enforces
  // the same policy server-side.
  useTelavoxCallStream();
  useWorkspaceAutoSave();

  // Pull the popup-duration from settings so the toast-duration is admin-
  // tunable without a client rebuild. Falls back to 10s while the query
  // is in flight. Cached aggressively — the setting rarely changes.
  const notificationSettings = useQuery({
    queryKey: ["settings", "notifications"],
    queryFn: () => settingsApi.notifications(),
    staleTime: 5 * 60_000,
  });
  const popupDurationMs = (notificationSettings.data?.popupDurationSeconds ?? 10) * 1000;
  useNotificationSignalR(popupDurationMs);
  // v0.0.84 — Timesheet → Comments per-user push (red dots + inbox refresh).
  useTimesheetCommentsSignalR();

  const secondarySidebar = useSecondarySidebarStore((s) => s.content);
  const { user } = useAuth();

  return (
    <div className="app-background relative flex h-screen overflow-hidden" data-testid="app-shell">
      <Sidebar />
      {secondarySidebar}
      <div className="flex min-w-0 flex-1 flex-col">
        <MaintenanceBanner variant="shell" />
        <CriticalBanner />
        <main className="flex-1 min-h-0 px-6 pt-6 pb-3 overflow-y-auto flex flex-col">
          <Outlet />
        </main>
      </div>
      <Toaster theme="dark" position="bottom-right" />
      <IncomingCallPopup />
      {user?.adsolutOrdersEnabled && <OrderPillHost />}
    </div>
  );
}
