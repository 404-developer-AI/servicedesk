import { SlidersHorizontal } from "lucide-react";
import { Badge } from "@/components/ui/badge";

export function GeneralSettingsPage() {
  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <SlidersHorizontal className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">General</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Branding, localization and other app-wide knobs. Nothing to tune here yet — global
            options land as the surrounding features stabilize.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <div className="glass-card p-10 text-center">
        <p className="text-sm text-muted-foreground">
          No general settings yet.
        </p>
      </div>
    </div>
  );
}
