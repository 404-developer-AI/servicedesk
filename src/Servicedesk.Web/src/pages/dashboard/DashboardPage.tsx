import { HealthPill } from "@/components/health/HealthPill";
import { useCurrentRole } from "@/hooks/useCurrentRole";
import { useAuth } from "@/auth/authStore";
import { LayoutDashboard } from "lucide-react";
import { DASHBOARD_TILES, roleSatisfies } from "@/lib/dashboardTiles";
import { cn } from "@/lib/utils";

export function DashboardPage() {
  const role = useCurrentRole();
  const { user } = useAuth();
  const enabled = new Set(user?.dashboardTiles ?? []);

  const visibleTiles = DASHBOARD_TILES.filter(
    (tile) => enabled.has(tile.id) && roleSatisfies(role, tile.minRole),
  );

  return (
    <div className="flex flex-1 flex-col gap-4">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-display-md font-semibold text-foreground">Dashboard</h1>
        <HealthPill />
      </div>

      {visibleTiles.length === 0 ? (
        <EmptyDashboard />
      ) : (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          {visibleTiles.map((tile) => {
            const Tile = tile.Component;
            return (
              <div
                key={tile.id}
                // h-full so the tile section inside (`h-full flex-col`)
                // stretches to the grid row's height — keeps side-by-side
                // tiles visually aligned even when their content has
                // different intrinsic heights (a sparse pickup-list next
                // to a six-row health card).
                className={cn("h-full", tile.span === "full" && "lg:col-span-2")}
              >
                <Tile />
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function EmptyDashboard() {
  return (
    <div className="glass-panel mx-auto mt-8 flex max-w-xl flex-col items-center gap-3 px-8 py-12 text-center">
      <div className="flex h-12 w-12 items-center justify-center rounded-2xl border border-white/10 bg-white/[0.04]">
        <LayoutDashboard className="h-5 w-5 text-muted-foreground" />
      </div>
      <h2 className="text-base font-semibold text-foreground">
        Your dashboard is empty
      </h2>
      <p className="max-w-sm text-sm text-muted-foreground">
        No dashboard tiles have been enabled for your account yet. Ask an
        administrator to enable tiles via Settings → Users → Features.
      </p>
    </div>
  );
}
