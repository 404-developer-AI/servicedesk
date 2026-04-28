import { useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { Plug } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import {
  IntegrationTile,
  type IntegrationStatus,
} from "@/components/integrations/IntegrationTile";
import { adsolutApi, type AdsolutState } from "@/lib/api";
import adsolutLogo from "@/assets/integrations/adsolut.ico";
import trmmLogo from "@/assets/integrations/trmm.png";
import zammadLogo from "@/assets/integrations/zammad.svg";

function tileStatusFor(state: AdsolutState | undefined): IntegrationStatus {
  switch (state) {
    case "connected":
      return "online";
    case "refresh_failed":
      return "error";
    default:
      return "not-configured";
  }
}

export function IntegrationsSettingsPage() {
  const navigate = useNavigate();
  const adsolutStatus = useQuery({
    queryKey: ["integrations", "adsolut", "status"] as const,
    queryFn: () => adsolutApi.status(),
    // Slightly stale data is fine — the detail page re-queries on mount.
    staleTime: 30_000,
  });

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

      <section
        aria-label="Available integrations"
        className="grid grid-cols-2 gap-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5"
      >
        <IntegrationTile
          name="Adsolut CRM"
          logo={adsolutLogo}
          variant="icon"
          status={tileStatusFor(adsolutStatus.data?.state)}
          onClick={() => navigate({ to: "/settings/integrations/adsolut" })}
        />
        <IntegrationTile name="Tactical RMM" logo={trmmLogo} variant="icon" status="not-configured" />
        <IntegrationTile name="Zammad Servicedesk" logo={zammadLogo} variant="icon" status="not-configured" />
      </section>
    </div>
  );
}
