import { Plug } from "lucide-react";
import { Badge } from "@/components/ui/badge";

export function IntegrationsSettingsPage() {
  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <Plug className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Integrations</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            External data sources and outbound integrations land here. Microsoft Graph for
            mail intake is configured under <strong>Settings → Mail</strong>, next to the
            polling and retention knobs it controls.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <section className="rounded-lg border border-dashed border-white/[0.08] bg-white/[0.02] p-8 text-center">
        <p className="text-sm text-muted-foreground">
          No integrations available yet. MSSQL read-only access and external API connectors
          are planned for v0.0.9.
        </p>
      </section>
    </div>
  );
}
