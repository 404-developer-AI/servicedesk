import { useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { ArrowDown, Plug, Sparkles } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import {
  IntegrationTile,
  type IntegrationStatus,
} from "@/components/integrations/IntegrationTile";
import { useIntegrationsSignalR } from "@/hooks/useIntegrationsSignalR";
import { adsolutApi, type AdsolutState } from "@/lib/api";
import adsolutLogo from "@/assets/integrations/adsolut.ico";
import trmmLogo from "@/assets/integrations/trmm.png";
import zammadLogo from "@/assets/integrations/zammad.svg";

function tileStatusFor(state: AdsolutState | undefined): IntegrationStatus {
  switch (state) {
    case "connected":
      return "online";
    case "sync_failing":
      return "warning";
    case "refresh_failed":
      return "error";
    default:
      return "not-configured";
  }
}

export function IntegrationsSettingsPage() {
  const navigate = useNavigate();
  // Subscribe to /hubs/integrations push events so a connect / disconnect
  // / refresh-failure on another tab flips the tile here within seconds
  // instead of waiting for the 30-second poll fallback.
  useIntegrationsSignalR();

  const adsolutStatus = useQuery({
    queryKey: ["integrations", "adsolut", "status"] as const,
    queryFn: () => adsolutApi.status(),
    // Slightly stale data is fine — the detail page re-queries on mount.
    staleTime: 30_000,
  });

  // Connected = any integration that has actually completed its connect
  // flow. Today only Adsolut has a real backend; Tactical RMM + Zammad
  // are placeholders that always read "not_configured" so they never tip
  // this counter. Once a second integration goes live, OR them in here.
  const adsolutTileStatus = tileStatusFor(adsolutStatus.data?.state);
  // "Connected" for the counter = OAuth is healthy. A sync_failing tile
  // still counts because the connection itself works; only the data pull
  // is degraded, which the amber pill on the tile already communicates.
  const connectedCount =
    adsolutTileStatus === "online" || adsolutTileStatus === "warning" ? 1 : 0;
  const totalCount = 3;
  const noneConfigured = connectedCount === 0;

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

      {noneConfigured ? (
        <div className="rounded-lg border border-white/[0.08] bg-gradient-to-br from-white/[0.04] to-white/[0.01] p-5">
          <div className="flex items-start gap-3">
            <div className="rounded-md border border-primary/20 bg-primary/[0.08] p-2 text-primary">
              <Sparkles className="h-4 w-4" />
            </div>
            <div className="flex-1 space-y-1">
              <h2 className="text-sm font-medium text-foreground">
                No integrations connected yet
              </h2>
              <p className="max-w-xl text-xs text-muted-foreground">
                Pick a tile below to start. Adsolut authorises with one click and stores
                only an encrypted refresh token; the others are listed as placeholders for
                upcoming releases.
              </p>
              <p className="flex items-center gap-1.5 pt-1 text-[11px] text-muted-foreground/60">
                <ArrowDown className="h-3 w-3" />
                Available connectors
              </p>
            </div>
          </div>
        </div>
      ) : (
        <div className="text-xs text-muted-foreground/70">
          {connectedCount} of {totalCount} integrations connected.
        </div>
      )}

      <section
        aria-label="Available integrations"
        className="grid grid-cols-2 gap-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5"
      >
        <IntegrationTile
          name="Adsolut CRM"
          logo={adsolutLogo}
          variant="icon"
          status={adsolutTileStatus}
          onClick={() => navigate({ to: "/settings/integrations/adsolut" })}
        />
        <IntegrationTile name="Tactical RMM" logo={trmmLogo} variant="icon" status="not-configured" />
        <IntegrationTile name="Zammad Servicedesk" logo={zammadLogo} variant="icon" status="not-configured" />
      </section>
    </div>
  );
}
