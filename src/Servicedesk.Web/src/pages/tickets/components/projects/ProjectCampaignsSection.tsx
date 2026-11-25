import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Megaphone, Plus } from "lucide-react";
import { NewCampaignDialog } from "@/components/campaigns/NewCampaignDialog";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { campaignKeys, campaignsApi } from "@/lib/campaigns-api";

/// v0.1.67 — Campaigns on a project ticket (Campaigns flag holders only):
/// the campaigns linked to this project with when they last sent, and
/// "Start campaign" — the project's open linked tickets become the
/// recipients. The project itself never mails anyone; each linked ticket
/// gets its own mail.
export function ProjectCampaignsSection({ projectTicketId, defaultName }: { projectTicketId: string; defaultName: string }) {
  const zone = useServerTimeZone();
  const [creating, setCreating] = React.useState(false);
  const list = useQuery({
    queryKey: campaignKeys.forProject(projectTicketId),
    queryFn: () => campaignsApi.list(true, projectTicketId),
    staleTime: 30_000,
  });
  const items = list.data ?? [];

  return (
    <section className="sd-project-campaigns">
      <div className="mb-1.5 flex items-center gap-2">
        <span className="text-[10px] uppercase tracking-wider text-muted-foreground/70">Campaigns</span>
        <button
          type="button"
          onClick={() => setCreating(true)}
          className="ml-auto inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[11px] text-primary hover:bg-primary/10"
        >
          <Plus className="h-3 w-3" /> Start campaign
        </button>
      </div>
      {items.length === 0 ? (
        <p className="text-[11px] text-muted-foreground/70">
          Mail every open linked ticket at once — one mail per ticket, sent from that ticket.
        </p>
      ) : (
        <ul className="space-y-1">
          {items.map((c) => (
            <li key={c.id}>
              <Link
                to="/campaigns/$campaignId"
                params={{ campaignId: c.id }}
                className="flex items-center gap-2 rounded-md border border-glass px-2 py-1.5 text-xs hover:bg-glass-hover"
              >
                <Megaphone className="h-3.5 w-3.5 shrink-0 text-primary" />
                <span className="min-w-0 flex-1 truncate font-medium">{c.name}</span>
                <span className="shrink-0 text-[10px] text-muted-foreground">
                  {c.lastSentUtc ? `Last sent ${formatDateTime(c.lastSentUtc, zone)}` : "Not sent yet"}
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
      <NewCampaignDialog
        open={creating}
        onOpenChange={setCreating}
        projectTicketId={projectTicketId}
        defaultName={defaultName}
      />
    </section>
  );
}
