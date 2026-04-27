import { Outlet } from "@tanstack/react-router";
import { Toaster } from "sonner";
import { useQuery } from "@tanstack/react-query";
import { Sidebar } from "@/shell/Sidebar";
import { CriticalBanner } from "@/components/health/CriticalBanner";
import { MaintenanceBanner } from "@/components/maintenance/MaintenanceBanner";
import { useSecondarySidebarStore } from "@/stores/useSecondarySidebarStore";
import { usePresenceConnection } from "@/hooks/usePresence";
import { useNotificationSignalR } from "@/hooks/useNotificationSignalR";
import { useWorkspaceAutoSave } from "@/hooks/useWorkspaceAutoSave";
import { settingsApi } from "@/lib/api";

export function AppShell() {
  usePresenceConnection();
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

  const secondarySidebar = useSecondarySidebarStore((s) => s.content);

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
    </div>
  );
}
