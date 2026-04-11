import { useState } from "react";
import { Outlet } from "@tanstack/react-router";
import { Toaster } from "sonner";
import { Sidebar } from "@/shell/Sidebar";
import { Header } from "@/shell/Header";
import { Footer } from "@/shell/Footer";
import { NewTicketFab } from "@/shell/NewTicketFab";
import { CommandPalette } from "@/shell/CommandPalette";

export function AppShell() {
  const [paletteOpen, setPaletteOpen] = useState(false);

  return (
    <div className="app-background relative flex min-h-screen" data-testid="app-shell">
      <Sidebar />
      <div className="flex min-w-0 flex-1 flex-col">
        <Header onOpenCommandPalette={() => setPaletteOpen(true)} />
        <main className="flex-1 px-6 pb-6">
          <Outlet />
        </main>
        <Footer />
      </div>
      <NewTicketFab />
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <Toaster theme="dark" position="bottom-right" />
    </div>
  );
}
