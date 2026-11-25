import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Archive, FolderKanban, Megaphone, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { ViewTabs } from "@/components/ViewTabs";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { formatNumber } from "@/lib/format";
import { campaignKeys, campaignsApi, type CampaignListItem } from "@/lib/campaigns-api";
import { NewCampaignDialog } from "@/components/campaigns/NewCampaignDialog";

/// v0.1.67 — the Campaigns list. A campaign mails many tickets at once (one
/// mail per ticket, sent from its own ticket) and tracks the answers. Route
/// and API are gated by the per-user Campaigns flag.
export function CampaignsPage() {
  const zone = useServerTimeZone();
  const [tab, setTab] = React.useState<"current" | "archived">("current");
  const [creating, setCreating] = React.useState(false);
  const list = useQuery({
    queryKey: campaignKeys.list(tab === "archived"),
    queryFn: () => campaignsApi.list(tab === "archived"),
    staleTime: 15_000,
  });
  const items = (list.data ?? []).filter((c) => (tab === "archived" ? c.state === "Archived" : c.state !== "Archived"));

  return (
    <div className="sd-campaigns flex min-h-0 w-full flex-1 flex-col gap-5">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">Campaigns</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            Mail many tickets at once — for example the half-yearly update request — with one personalised mail per
            ticket, sent from that ticket. Follow up who answered, who still has to, and whose timer ran out.
          </p>
        </div>
        <Button onClick={() => setCreating(true)}>
          <Plus className="mr-1.5 h-4 w-4" /> New campaign
        </Button>
      </header>

      <ViewTabs
        ariaLabel="Campaigns"
        value={tab}
        onChange={(id) => setTab(id as "current" | "archived")}
        tabs={[
          { id: "current", label: "Current", icon: Megaphone },
          { id: "archived", label: "Archived", icon: Archive },
        ]}
      />

      <div className="glass-panel min-h-64 flex-1 overflow-auto">
        {list.isLoading ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : list.isError ? (
          <div className="p-6 text-sm text-destructive">Could not load the campaigns. Refresh to try again.</div>
        ) : items.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
            <Megaphone className="h-6 w-6 text-muted-foreground" />
            <p className="max-w-md text-sm text-muted-foreground">
              {tab === "archived"
                ? "No archived campaigns."
                : "No campaigns yet. Start one here, from a selection in the ticket list, or from a project ticket."}
            </p>
          </div>
        ) : (
          <table className="w-full text-sm">
            <thead className="sticky top-0 z-10 bg-popover">
              <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                <th className="px-4 py-2.5 font-medium">Campaign</th>
                <th className="px-3 py-2.5 font-medium">Tickets</th>
                <th className="px-3 py-2.5 font-medium">Sent</th>
                <th className="px-3 py-2.5 font-medium">Last sent</th>
                <th className="px-4 py-2.5 font-medium">Created by</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-glass">
              {items.map((c) => <Row key={c.id} item={c} zone={zone} />)}
            </tbody>
          </table>
        )}
      </div>

      <NewCampaignDialog open={creating} onOpenChange={setCreating} />
    </div>
  );
}

function Row({ item, zone }: { item: CampaignListItem; zone: string | null }) {
  return (
    <tr className="sd-campaign-row hover:bg-glass/60">
      <td className="px-4 py-2.5">
        <Link to="/campaigns/$campaignId" params={{ campaignId: item.id }} className="font-medium text-foreground hover:text-primary">
          {item.name}
        </Link>
        <div className="mt-0.5 flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground">
          <span>{item.state === "Draft" ? "Draft" : item.state === "Archived" ? "Archived" : "Active"}</span>
          {item.projectTicketNumber !== null && (
            <span className="inline-flex items-center gap-1">
              <FolderKanban className="h-3 w-3" /> Project #{item.projectTicketNumber}
            </span>
          )}
        </div>
      </td>
      <td className="px-3 py-2.5 tabular-nums">{formatNumber(item.ticketCount)}</td>
      <td className="px-3 py-2.5 tabular-nums">
        {formatNumber(item.sentCount)}
        {item.queuedCount > 0 && <span className="ml-2 text-[11px] text-primary">{formatNumber(item.queuedCount)} queued</span>}
        {item.failedCount > 0 && <span className="ml-2 text-[11px] text-destructive">{formatNumber(item.failedCount)} failed</span>}
      </td>
      <td className="px-3 py-2.5 text-muted-foreground">{formatDateTime(item.lastSentUtc, zone)}</td>
      <td className="px-4 py-2.5 text-muted-foreground">{item.createdByEmail ?? "—"}</td>
    </tr>
  );
}
