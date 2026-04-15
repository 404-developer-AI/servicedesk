import { HealthPill } from "@/components/health/HealthPill";
import { AvgPickupTile } from "@/components/dashboard/AvgPickupTile";

export function DashboardPage() {
  return (
    <div className="flex flex-1 flex-col gap-4">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-display-md font-semibold text-foreground">Dashboard</h1>
        <HealthPill />
      </div>
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <AvgPickupTile />
      </div>
    </div>
  );
}
