import { Timer } from "lucide-react";
import { Badge } from "@/components/ui/badge";

export function SlaSettingsPage() {
  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <Timer className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">SLA</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Response and resolution targets, business hours and escalation policies. Wired up in
            v0.0.8 step 7 once mail intake has settled.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Coming in v0.0.8
        </Badge>
      </header>

      <div className="glass-card p-10 text-center">
        <p className="text-sm text-muted-foreground">
          SLA configuration arrives with the SLA engine.
        </p>
      </div>
    </div>
  );
}
