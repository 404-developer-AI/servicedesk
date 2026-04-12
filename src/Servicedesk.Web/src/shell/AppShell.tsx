import { useState } from "react";
import { Outlet } from "@tanstack/react-router";
import { Toaster } from "sonner";
import { Sidebar } from "@/shell/Sidebar";
import { Header } from "@/shell/Header";
import { CommandPalette } from "@/shell/CommandPalette";
import { useSecondarySidebarStore } from "@/stores/useSecondarySidebarStore";
import { usePresenceConnection } from "@/hooks/usePresence";

export function AppShell() {
  usePresenceConnection();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const secondarySidebar = useSecondarySidebarStore((s) => s.content);

  return (
    <div className="app-background relative flex min-h-screen" data-testid="app-shell">
      <Sidebar />
      {secondarySidebar}
      <div className="flex min-w-0 flex-1 flex-col">
        <Header onOpenCommandPalette={() => setPaletteOpen(true)} />
        <main className="flex-1 px-6 pb-6">
          <Outlet />
        </main>
      </div>
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <Toaster theme="dark" position="bottom-right" />
    </div>
  );
}
