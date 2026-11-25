import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useParams } from "@tanstack/react-router";
import {
  Archive, ArchiveRestore, ArrowLeft, Copy, Eye, FolderKanban, History, Inbox, ListPlus, Mail, MoreHorizontal,
  PenLine, RotateCcw, Send, Trash2, Wrench, XCircle,
} from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ViewTabs } from "@/components/ViewTabs";
import { useAuth } from "@/auth/authStore";
import { useCampaignsSignalR } from "@/hooks/useCampaignsSignalR";
import { errorMessage } from "@/lib/http";
import {
  CAMPAIGNS_KEY, campaignKeys, campaignsApi, type CampaignDetail, type CampaignRoundKind,
} from "@/lib/campaigns-api";
import { CampaignComposer } from "./components/CampaignComposer";
import { CampaignRecipientTable } from "./components/CampaignRecipientTable";
import { CampaignPreviewDialog } from "./components/CampaignPreviewDialog";
import { CampaignSendDialog } from "./components/CampaignSendDialog";
import { CampaignRoundHistory } from "./components/CampaignRoundHistory";

type Tab = "recipients" | "message" | "history";

/// v0.1.67 — one campaign: who gets the mail and how they answered (the
/// dashboard), the message, and the round history. Replies are derived on
/// the server from each ticket's timeline; the page refetches on the
/// CampaignChanged push and once a minute to pick up new answers.
export function CampaignDetailPage() {
  const { campaignId } = useParams({ from: "/campaigns/$campaignId" });
  const { user } = useAuth();
  useCampaignsSignalR(!!user?.campaignsEnabled);
  const detail = useQuery({
    queryKey: campaignKeys.detail(campaignId),
    queryFn: () => campaignsApi.get(campaignId),
    refetchInterval: 60_000,
    refetchOnWindowFocus: true,
  });
  const settings = useQuery({ queryKey: campaignKeys.settings(), queryFn: campaignsApi.settings, staleTime: 60_000 });

  if (detail.isLoading) {
    return (
      <div className="flex w-full flex-col gap-4">
        <Skeleton className="h-8 w-1/3" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }
  if (detail.isError || !detail.data) {
    return (
      <div className="space-y-3">
        <BackLink />
        <p className="text-sm text-destructive">{errorMessage(detail.error, "This campaign could not be loaded.")}</p>
      </div>
    );
  }
  return <CampaignView campaign={detail.data} sendingEnabled={settings.data?.enabled !== false} settings={settings.data} />;
}

function BackLink() {
  return (
    <Link to="/campaigns" className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
      <ArrowLeft className="h-3.5 w-3.5" /> Campaigns
    </Link>
  );
}

function CampaignView({
  campaign, sendingEnabled, settings,
}: {
  campaign: CampaignDetail;
  sendingEnabled: boolean;
  settings: Parameters<typeof CampaignSendDialog>[0]["settings"];
}) {
  const qc = useQueryClient();
  const navigate = useNavigate();
  const archived = campaign.state === "Archived";
  const hasRounds = campaign.rounds.length > 0;
  const mailed = campaign.recipients.some((r) => r.firstSentUtc);
  const [tab, setTab] = React.useState<Tab>(() => (campaign.subject || campaign.bodyHtml ? "recipients" : "message"));
  const [sendKind, setSendKind] = React.useState<CampaignRoundKind | null>(null);
  const [previewId, setPreviewId] = React.useState<string | null>(null);
  const [renaming, setRenaming] = React.useState(false);
  const [name, setName] = React.useState(campaign.name);
  React.useEffect(() => setName(campaign.name), [campaign.name]);

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: campaignKeys.detail(campaign.id) });
    void qc.invalidateQueries({ queryKey: [...CAMPAIGNS_KEY, "list"] });
  };
  const run = useMutation({
    mutationFn: async (action: "archive" | "restore" | "rename" | "duplicate" | "delete" | "cancel" | "retry" | "project") => {
      switch (action) {
        case "archive": await campaignsApi.update(campaign.id, { state: "Archived" }); return "Campaign archived";
        case "restore": await campaignsApi.update(campaign.id, { state: hasRounds ? "Active" : "Draft" }); return "Campaign restored";
        case "rename": await campaignsApi.update(campaign.id, { name: name.trim() }); setRenaming(false); return "Renamed";
        case "duplicate": {
          const r = await campaignsApi.duplicate(campaign.id);
          void navigate({ to: "/campaigns/$campaignId", params: { campaignId: r.id } });
          return `Copy created with ${r.added.added} tickets`;
        }
        case "delete": await campaignsApi.remove(campaign.id); void navigate({ to: "/campaigns" }); return "Campaign deleted";
        case "cancel": { const r = await campaignsApi.cancelQueued(campaign.id); return `${r.cancelled} queued mail(s) cancelled`; }
        case "retry": { const r = await campaignsApi.retryFailed(campaign.id); return `${r.retried} mail(s) queued again`; }
        case "project": {
          const r = await campaignsApi.addProjectTickets(campaign.id);
          return `${r.added} linked ticket(s) added${r.notAllowed > 0 ? ` · ${r.notAllowed} left out` : ""}`;
        }
      }
    },
    onSuccess: (msg) => { toast.success(msg); refresh(); },
    onError: (err) => toast.error(errorMessage(err, "That did not work")),
  });

  const queued = campaign.rounds.reduce((n, r) => n + r.queued, 0);
  const failed = campaign.recipients.filter((r) => r.state === "Failed").length;
  const notSent = campaign.recipients.filter((r) => r.state === "NotSent").length;
  const messageReady = !!campaign.subject.trim() && !!campaign.bodyHtml.replace(/<[^>]*>/g, "").trim();

  return (
    <div className="sd-campaign flex min-h-0 w-full flex-1 flex-col gap-5">
      <BackLink />
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 space-y-1">
          {renaming ? (
            <form className="flex items-center gap-2" onSubmit={(e) => { e.preventDefault(); if (name.trim()) run.mutate("rename"); }}>
              <Input value={name} maxLength={200} autoFocus onChange={(e) => setName(e.target.value)} className="h-9 w-80" />
              <Button size="sm" type="submit" disabled={!name.trim()}>Save</Button>
              <Button size="sm" variant="ghost" type="button" onClick={() => { setRenaming(false); setName(campaign.name); }}>Cancel</Button>
            </form>
          ) : (
            <h1 className="flex items-center gap-2 text-display-md font-semibold text-foreground">
              <span className="truncate">{campaign.name}</span>
              {!archived && (
                <button type="button" aria-label="Rename" onClick={() => setRenaming(true)} className="text-muted-foreground hover:text-foreground">
                  <PenLine className="h-4 w-4" />
                </button>
              )}
            </h1>
          )}
          <div className="flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
            <span>{archived ? "Archived" : hasRounds ? "Active" : "Draft"}</span>
            <span>{campaign.recipients.length + campaign.hiddenCount} tickets</span>
            {campaign.project && (
              <span className="inline-flex items-center gap-1">
                <FolderKanban className="h-3.5 w-3.5" />
                {campaign.project.visible && campaign.project.number !== null ? (
                  <Link to="/tickets/$ticketId" params={{ ticketId: campaign.project.id }} className="hover:text-foreground">
                    Project #{campaign.project.number} {campaign.project.subject}
                  </Link>
                ) : "Linked project"}
              </span>
            )}
            {campaign.maintenance && (
              <Link to="/maintenance" search={{ plan: campaign.maintenance.planId } as never} className="inline-flex items-center gap-1 hover:text-foreground">
                <Wrench className="h-3.5 w-3.5" /> Maintenance: {campaign.maintenance.planName}
              </Link>
            )}
            {campaign.createdByEmail && <span>Created by {campaign.createdByEmail}</span>}
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {campaign.recipients.length > 0 && (
            <Button variant="outline" onClick={() => setPreviewId(campaign.recipients[0].ticketId)}>
              <Eye className="mr-1.5 h-4 w-4" /> Preview
            </Button>
          )}
          {!archived && mailed && (
            <Button variant="outline" disabled={!sendingEnabled || !messageReady} onClick={() => setSendKind("FollowUp")}>
              <Mail className="mr-1.5 h-4 w-4" /> Follow-up
            </Button>
          )}
          {!archived && (
            <Button
              disabled={!sendingEnabled || !messageReady || notSent === 0}
              title={!messageReady ? "Write a subject and a message first" : notSent === 0 ? "Every ticket was mailed" : undefined}
              onClick={() => setSendKind("Initial")}
            >
              <Send className="mr-1.5 h-4 w-4" /> Send{notSent > 0 ? ` to ${notSent}` : ""}
            </Button>
          )}
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" aria-label="More actions"><MoreHorizontal className="h-4 w-4" /></Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-56">
              {campaign.project?.visible && !archived && (
                <DropdownMenuItem onSelect={() => run.mutate("project")}>
                  <ListPlus className="mr-2 h-4 w-4" /> Add the project's open tickets
                </DropdownMenuItem>
              )}
              {queued > 0 && (
                <DropdownMenuItem onSelect={() => run.mutate("cancel")}>
                  <XCircle className="mr-2 h-4 w-4" /> Cancel queued mails
                </DropdownMenuItem>
              )}
              {failed > 0 && (
                <DropdownMenuItem onSelect={() => run.mutate("retry")}>
                  <RotateCcw className="mr-2 h-4 w-4" /> Retry failed mails
                </DropdownMenuItem>
              )}
              <DropdownMenuItem onSelect={() => run.mutate("duplicate")}>
                <Copy className="mr-2 h-4 w-4" /> Duplicate for a next round
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              {archived ? (
                <DropdownMenuItem onSelect={() => run.mutate("restore")}>
                  <ArchiveRestore className="mr-2 h-4 w-4" /> Restore
                </DropdownMenuItem>
              ) : (
                <DropdownMenuItem onSelect={() => run.mutate("archive")}>
                  <Archive className="mr-2 h-4 w-4" /> Archive
                </DropdownMenuItem>
              )}
              {!hasRounds && (
                <DropdownMenuItem className="text-destructive" onSelect={() => run.mutate("delete")}>
                  <Trash2 className="mr-2 h-4 w-4" /> Delete
                </DropdownMenuItem>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </header>

      {!sendingEnabled && (
        <p className="rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground">
          Campaigns are switched off in Settings — nothing is sent until an admin turns them back on.
        </p>
      )}

      <ViewTabs
        ariaLabel="Campaign"
        value={tab}
        onChange={(id) => setTab(id as Tab)}
        tabs={[
          { id: "recipients", label: "Tickets & answers", icon: Inbox, count: campaign.recipients.filter((r) => !r.decision && (r.state === "ReplyReceived" || r.state === "TimerExpired")).length || undefined },
          { id: "message", label: "Message", icon: PenLine },
          { id: "history", label: "History", icon: History },
        ]}
      />

      {tab === "recipients" && (
        <CampaignRecipientTable
          campaignId={campaign.id}
          recipients={campaign.recipients}
          hiddenCount={campaign.hiddenCount}
          readOnly={archived}
          onPreview={setPreviewId}
        />
      )}
      {tab === "message" && (
        <div className="glass-panel p-5">
          {hasRounds && (
            <p className="mb-4 text-xs text-muted-foreground">
              Changes apply to rounds you send from now on; mails already sent stay as they were.
            </p>
          )}
          <CampaignComposer campaign={campaign} readOnly={archived} />
        </div>
      )}
      {tab === "history" && <CampaignRoundHistory rounds={campaign.rounds} />}

      {sendKind && (
        <CampaignSendDialog
          campaignId={campaign.id}
          kind={sendKind}
          open={!!sendKind}
          onOpenChange={(o) => { if (!o) setSendKind(null); }}
          settings={settings}
        />
      )}
      <CampaignPreviewDialog
        campaignId={campaign.id}
        ticketIds={campaign.recipients.map((r) => r.ticketId)}
        ticketId={previewId}
        onTicketChange={setPreviewId}
        onClose={() => setPreviewId(null)}
      />
    </div>
  );
}
