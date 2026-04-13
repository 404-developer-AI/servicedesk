import { Outlet } from "@tanstack/react-router";
import { Toaster } from "sonner";
import { Sidebar } from "@/shell/Sidebar";
import { useSecondarySidebarStore } from "@/stores/useSecondarySidebarStore";
import { usePresenceConnection } from "@/hooks/usePresence";
import { useWorkspaceAutoSave } from "@/hooks/useWorkspaceAutoSave";

export function AppShell() {
  usePresenceConnection();
  useWorkspaceAutoSave();
  const secondarySidebar = useSecondarySidebarStore((s) => s.content);

  return (
    <div className="app-background relative flex h-screen overflow-hidden" data-testid="app-shell">
      <Sidebar />
      {secondarySidebar}
      <div className="flex min-w-0 flex-1 flex-col">
        <main className="flex-1 min-h-0 px-6 pt-6 pb-3 overflow-y-auto flex flex-col">
          <Outlet />
        </main>
      </div>
      <Toaster theme="dark" position="bottom-right" />
    </div>
  );
}
