import { useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { ArrowDown, Plug, Sparkles } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import {
  IntegrationTile,
  type IntegrationStatus,
} from "@/components/integrations/IntegrationTile";
import {
  adsolutApi,
  m365AdminApi,
  telavoxAdminApi,
  trmmAdminApi,
  zammadAdminApi,
  type AdsolutState,
  type M365ConnectionState,
  type TelavoxConnectionState,
  type TrmmConnectionState,
  type ZammadConnectionState,
} from "@/lib/api";
import adsolutLogo from "@/assets/integrations/adsolut.ico";
import microsoftLogo from "@/assets/integrations/microsoft.svg";
import telavoxLogo from "@/assets/integrations/telavox.svg";
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

function telavoxTileStatus(
  state: TelavoxConnectionState | undefined,
): IntegrationStatus {
  switch (state) {
    case "Active":
      return "online";
    case "Ready":
    case "NoCustomerSelected":
    case "NotConfigured":
      return "warning";
    default:
      return "not-configured";
  }
}

function zammadTileStatus(
  state: ZammadConnectionState | undefined,
): IntegrationStatus {
  switch (state) {
    case "Ready":
      return "online";
    case "NotConfigured":
      return "warning";
    default:
      return "not-configured";
  }
}

function trmmTileStatus(state: TrmmConnectionState | undefined): IntegrationStatus {
  switch (state) {
    case "ready":
      return "online";
    case "not-configured":
      return "warning";
    default:
      return "not-configured";
  }
}

function m365TileStatus(state: M365ConnectionState | undefined): IntegrationStatus {
  switch (state) {
    case "ready":
      return "online";
    case "not-configured":
      return "warning";
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
  const telavoxStatus = useQuery({
    queryKey: ["integrations", "telavox", "status"] as const,
    queryFn: () => telavoxAdminApi.status(),
    staleTime: 30_000,
  });
  const zammadStatus = useQuery({
    queryKey: ["integrations", "zammad", "status"] as const,
    queryFn: () => zammadAdminApi.status(),
    staleTime: 30_000,
  });
  const trmmStatus = useQuery({
    queryKey: ["integrations", "trmm", "status"] as const,
    queryFn: () => trmmAdminApi.status(),
    staleTime: 30_000,
  });
  const m365Status = useQuery({
    queryKey: ["integrations", "m365", "status"] as const,
    queryFn: () => m365AdminApi.status(),
    staleTime: 30_000,
  });

  const adsolutTileStatus = tileStatusFor(adsolutStatus.data?.state);
  const telavoxStatusTile = telavoxTileStatus(telavoxStatus.data?.state);
  const zammadStatusTile = zammadTileStatus(zammadStatus.data?.state);
  const trmmStatusTile = trmmTileStatus(trmmStatus.data?.state);
  const m365StatusTile = m365TileStatus(m365Status.data?.state);
  const tilesConfigured = [
    adsolutTileStatus,
    telavoxStatusTile,
    zammadStatusTile,
    trmmStatusTile,
    m365StatusTile,
  ].filter((s) => s === "online" || s === "warning").length;
  const totalCount = 5;
  const noneConfigured = tilesConfigured === 0;
  const connectedCount = tilesConfigured;

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
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      {noneConfigured ? (
        <div className="rounded-lg border border-glass-strong bg-gradient-to-br from-white/[0.04] to-white/[0.01] p-5">
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
        <IntegrationTile
          name="Telavox"
          logo={telavoxLogo}
          variant="icon"
          status={telavoxStatusTile}
          onClick={() => navigate({ to: "/settings/integrations/telavox" })}
        />
        <IntegrationTile
          name="Tactical RMM"
          logo={trmmLogo}
          variant="icon"
          status={trmmStatusTile}
          onClick={() => navigate({ to: "/settings/integrations/trmm" })}
        />
        <IntegrationTile
          name="Zammad Servicedesk"
          logo={zammadLogo}
          variant="icon"
          status={zammadStatusTile}
          onClick={() => navigate({ to: "/settings/integrations/zammad" })}
        />
        <IntegrationTile
          name="Microsoft 365"
          logo={microsoftLogo}
          variant="icon"
          imgClassName="max-h-20 max-w-[80%]"
          status={m365StatusTile}
          onClick={() => navigate({ to: "/settings/integrations/m365" })}
        />
      </section>
    </div>
  );
}
