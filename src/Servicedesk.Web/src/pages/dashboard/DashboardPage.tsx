import { Badge } from "@/components/ui/badge";
import { MeshSurface } from "@/shell/MeshBackground";
import { HealthPill } from "@/components/health/HealthPill";
import { findNavItem } from "@/shell/navItems";

export function DashboardPage() {
  const item = findNavItem("/");
  return (
    <div className="flex flex-1 flex-col gap-4">
      <div className="flex justify-end">
        <HealthPill />
      </div>
      <div className="relative flex flex-1 items-center justify-center p-8">
        <MeshSurface className="absolute inset-0" />
        <div className="glass-card relative z-10 w-full max-w-xl p-10">
          <div className="flex items-start justify-between gap-4">
            <div className="space-y-2">
              <h1 className="text-display-md font-semibold text-foreground">
                {item?.label ?? "Dashboard"}
              </h1>
              <p className="text-sm text-muted-foreground">
                {item?.description ?? "Overview and quick actions."}
              </p>
            </div>
            {item?.comingIn ? (
              <Badge
                variant="secondary"
                className="shrink-0 border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground"
              >
                Coming in {item.comingIn}
              </Badge>
            ) : null}
          </div>
        </div>
      </div>
    </div>
  );
}
