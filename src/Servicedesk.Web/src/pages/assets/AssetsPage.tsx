import { useNavigate, useSearch } from "@tanstack/react-router";
import { MonitorCheck, Server } from "lucide-react";
import { cn } from "@/lib/utils";
import { useAssetsRealtime } from "@/hooks/useAssetsRealtime";
import { AssetsInventoryTab } from "./AssetsInventoryTab";
import { RemoteDesktopTab } from "./RemoteDesktopTab";

type Tab = "assets" | "remote-desktop";

/// Top-level Assets page (v0.1.10). Two tabs over the Tactical RMM mirror:
///   1. **Assets** — the inventory list (hostname / OS build / EOL / client).
///   2. **Remote Desktop** — per-client view of the servers whose RDS
///      script check reports TRUE, plus an attention list for failed or
///      missing checks, and a notes thread per client.
///
/// The active tab lives in the URL (`?tab=remote-desktop`) so global
/// search can deep-link a client's notes and a reload lands on the same
/// tab. Both tabs share the `["assets"]` query prefix; one realtime
/// listener here refreshes whichever tab is mounted.
export function AssetsPage() {
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as {
    tab?: "remote-desktop";
    client?: number;
  };
  const tab: Tab = search.tab === "remote-desktop" ? "remote-desktop" : "assets";

  useAssetsRealtime();

  const setTab = (next: Tab) => {
    navigate({
      to: "/assets",
      search: next === "remote-desktop" ? { tab: "remote-desktop" } : {},
      replace: true,
    });
  };

  return (
    <div className="flex flex-col gap-6 p-6">
      <header className="space-y-2">
        <div className="mb-2 text-primary">
          <Server className="h-6 w-6" />
        </div>
        <h1 className="text-display-md font-semibold text-foreground">Assets</h1>
      </header>

      <div className="glass-panel flex items-center gap-1 p-1">
        <TabButton
          active={tab === "assets"}
          onClick={() => setTab("assets")}
          icon={<Server className="h-3.5 w-3.5" />}
          label="Assets"
        />
        <TabButton
          active={tab === "remote-desktop"}
          onClick={() => setTab("remote-desktop")}
          icon={<MonitorCheck className="h-3.5 w-3.5" />}
          label="Remote Desktop"
        />
      </div>

      {tab === "assets" && <AssetsInventoryTab />}
      {tab === "remote-desktop" && (
        <RemoteDesktopTab
          initialClientId={search.client ?? null}
          onClientConsumed={() =>
            navigate({ to: "/assets", search: { tab: "remote-desktop" }, replace: true })
          }
        />
      )}
    </div>
  );
}

function TabButton({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean;
  onClick: () => void;
  icon: React.ReactNode;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "relative inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-sm transition-colors",
        active
          ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
          : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
      )}
    >
      {icon}
      {label}
    </button>
  );
}
