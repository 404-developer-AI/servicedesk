import { useEffect } from "react";
import { Link } from "@tanstack/react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowLeft, Plug } from "lucide-react";
import { adsolutApi, settingsApi } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { ADSOLUT_SETTINGS_QUERY_KEY, ADSOLUT_STATUS_QUERY_KEY, ADSOLUT_SECRET_QUERY_KEY, ADSOLUT_SYNC_QUERY_KEY } from "@/pages/settings/adsolut/shared";
import { ConnectionSection } from "@/pages/settings/adsolut/ConnectionSection";
import { ConfigurationSection } from "@/pages/settings/adsolut/ConfigurationSection";
import { DossierSection } from "@/pages/settings/adsolut/DossierSection";
import { SyncSection } from "@/pages/settings/adsolut/SyncSection";
import { ApiDebugSection } from "@/pages/settings/adsolut/ApiDebugSection";
import { ErpSalesReceiptsPreviewSection } from "@/pages/settings/adsolut/ErpSalesReceiptsPreviewSection";
import { PutDebugSection } from "@/pages/settings/adsolut/PutDebugSection";
import { ExternalClientSection } from "@/pages/settings/adsolut/ExternalClientSection";
import { AuditLogSection } from "@/pages/settings/adsolut/AuditLogSection";
import { ErpSalesReceiptsPanel } from "@/pages/settings/adsolut/ErpSalesReceiptsPanel";
import { ErpOrdersPanel } from "@/pages/settings/adsolut/ErpOrdersPanel";
import { ErpArticlesPanel } from "@/pages/settings/adsolut/ErpArticlesPanel";
import { ErpContractsPanel } from "@/pages/settings/adsolut/ErpContractsPanel";

function statusFromQuery(): string | null {
  if (typeof window === "undefined") return null;
  const params = new URLSearchParams(window.location.search);
  return params.get("status");
}

function clearStatusFromUrl() {
  if (typeof window === "undefined") return;
  const url = new URL(window.location.href);
  if (!url.searchParams.has("status")) return;
  url.searchParams.delete("status");
  window.history.replaceState({}, "", url.toString());
}

export function AdsolutIntegrationPage() {
  const qc = useQueryClient();

  const status = useQuery({
    queryKey: ADSOLUT_STATUS_QUERY_KEY,
    queryFn: () => adsolutApi.status(),
  });
  const settingsList = useQuery({
    queryKey: ADSOLUT_SETTINGS_QUERY_KEY,
    queryFn: () => settingsApi.list("Adsolut"),
  });
  const secret = useQuery({
    queryKey: ADSOLUT_SECRET_QUERY_KEY,
    queryFn: () => adsolutApi.secretStatus(),
  });

  // Only fetch the dossier list once we are actually connected — no point
  // calling the Administrations API while the integration is in
  // not_configured / not_connected state (it would just 401).
  const isConnectedState =
    status.data?.state === "connected" ||
    status.data?.state === "refresh_failed" ||
    status.data?.state === "sync_failing";
  const syncState = useQuery({
    queryKey: ADSOLUT_SYNC_QUERY_KEY,
    queryFn: () => adsolutApi.syncState(),
    enabled: isConnectedState,
  });

  // Surface the post-callback ?status= query param as a toast, then strip it
  // from the URL so a refresh doesn't fire it again.
  useEffect(() => {
    const code = statusFromQuery();
    if (!code) return;
    if (code === "connected") {
      toast.success("Connected to Adsolut");
    } else {
      toast.error(`Adsolut connect failed: ${code.replaceAll("_", " ")}`);
    }
    clearStatusFromUrl();
    qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
  }, [qc]);

  if (status.isLoading || settingsList.isLoading || secret.isLoading) {
    return <Skeleton className="h-96 w-full" />;
  }

  const s = status.data;
  if (!s) {
    return (
      <div className="rounded-md border border-rose-400/30 bg-rose-500/8 p-4 text-sm text-rose-200">
        Could not load Adsolut status. Refresh the page or check the API logs.
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <Link
            to="/settings/integrations"
            className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground"
          >
            <ArrowLeft className="h-3.5 w-3.5" />
            Back to Integrations
          </Link>
          <div className="mb-2 text-primary">
            <Plug className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Adsolut CRM</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            One-time admin authorization against the Wolters Kluwer login. The granted
            refresh token is stored encrypted and rotated on every use; one Adsolut
            administration links to this servicedesk install.
          </p>
        </div>
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <ConnectionSection status={s} syncState={syncState.data} />

      {/* Configuration */}
      <ConfigurationSection
        status={s}
        settingsEntries={settingsList.data}
        secretConfigured={secret.data?.configured}
      />

      {/* v0.0.26 — Dossier picker. Only relevant once the integration is
          connected. The admin must explicitly activate one Adsolut
          administration before sync ticks do real work; the worker skips
          ticks while administrationId is null. */}
      {isConnectedState && <DossierSection status={s} />}

      {/* v0.0.26 — Sync panel. Counters + last-tick timestamps + a "Sync now"
          button so admins do not have to wait the full interval after
          changing a toggle. */}
      {isConnectedState && s.administrationId && (
        <SyncSection syncState={syncState.data} settingsEntries={settingsList.data} />
      )}

      {/* API debug — admin probe for /customers or /suppliers by Code. Only
          useful once a dossier is active because Adsolut returns nothing
          without one. Strict whitelist on the backend means this card cannot
          be turned into a generic API proxy. */}
      {isConnectedState && s.administrationId && <ApiDebugSection />}

      {/* ERP sales-receipts mirror controls — enable toggle, interval,
          dynamic status filter, sync state + "Sync now". Feeds the
          Timesheet → Adsolut tab. */}
      {isConnectedState && s.administrationId && (
        <ErpSalesReceiptsPanel
          settingsEntries={settingsList.data}
          settingsQueryKey={ADSOLUT_SETTINGS_QUERY_KEY}
        />
      )}

      {/* ERP orders mirror controls — enable toggle, interval, display-only
          status filter, sync state + "Sync now". Feeds the Orders overview
          (navbar → Assets → Orders). */}
      {isConnectedState && s.administrationId && (
        <ErpOrdersPanel
          settingsEntries={settingsList.data}
          settingsQueryKey={ADSOLUT_SETTINGS_QUERY_KEY}
        />
      )}

      {/* ERP articles mirror controls — enable toggle, interval, sync state +
          "Sync now". Feeds the Contract Articles list (Contracts → Contract
          Articles). */}
      {isConnectedState && s.administrationId && (
        <ErpArticlesPanel
          settingsEntries={settingsList.data}
          settingsQueryKey={ADSOLUT_SETTINGS_QUERY_KEY}
        />
      )}

      {/* ERP contracts mirror controls — enable toggle, interval, display-only
          status filter, sync state + "Sync now". Feeds the Contracts overview
          (Contracts → Contracts overview). */}
      {isConnectedState && s.administrationId && (
        <ErpContractsPanel
          settingsEntries={settingsList.data}
          settingsQueryKey={ADSOLUT_SETTINGS_QUERY_KEY}
        />
      )}

      {/* ERP sales-receipts preview — read-only probe of the ERP
          SalesReceiptInfos list endpoint (verkoopbonnen), the read path for
          the upcoming Timesheet → Adsolut tab. Surfaces the raw response so
          we can confirm the real `state` codes + which fields WK serialises
          before committing a mirror schema. Needs the WK.BE.Erp.Read scope
          on the active token (tick it in the scopes picker + reconnect) and
          the client subscribed to the ERP product at Wolters Kluwer. Each
          call lands in the audit log as debug.erp_sales_receipts. */}
      {isConnectedState && s.administrationId && <ErpSalesReceiptsPreviewSection />}

      {/* PUT debug — Postman-lite for /customers/{id}. Build the canonical
          PUT body from the upstream GET, optionally edit it in-place, then
          send. Each call lands in the audit log as debug.put_preview /
          debug.customer_put. */}
      {isConnectedState && s.administrationId && <PutDebugSection />}

      {/* External client — Postman / curl bring-your-own-debugger card.
          Reveals the current access token + base URL + administration id
          so an admin can paste them into a local debug tool for one-off
          API exploration without going through our wrappers. Token has
          ~1h validity; the same button re-fetches when expired. Each
          reveal is audited in the security audit_log. */}
      {isConnectedState && <ExternalClientSection />}

      {/* Audit log — operational call history (placeholder marker) */}
      <AuditLogSection />
    </div>
  );
}
