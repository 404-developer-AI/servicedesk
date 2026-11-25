import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Loader2, Megaphone, Plus } from "lucide-react";
import { toast } from "sonner";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";
import { CAMPAIGNS_KEY, campaignKeys, campaignsApi } from "@/lib/campaigns-api";
import { NewCampaignDialog } from "./NewCampaignDialog";

/// v0.1.67 — "Add to campaign…" from the ticket-list selection: pick a
/// current campaign or start a new one with the selected tickets. The server
/// keeps only tickets in the caller's queues (never project tickets).
export function AddToCampaignDialog({
  open,
  onOpenChange,
  ticketIds,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  ticketIds: string[];
}) {
  const [creating, setCreating] = React.useState(false);
  const [picked, setPicked] = React.useState<string | null>(null);
  const qc = useQueryClient();
  const navigate = useNavigate();
  const list = useQuery({
    queryKey: campaignKeys.list(false),
    queryFn: () => campaignsApi.list(false),
    enabled: open,
    staleTime: 15_000,
  });
  React.useEffect(() => { if (open) setPicked(null); }, [open]);

  const add = useMutation({
    mutationFn: (campaignId: string) => campaignsApi.addTickets(campaignId, ticketIds),
    onSuccess: (r, campaignId) => {
      void qc.invalidateQueries({ queryKey: CAMPAIGNS_KEY });
      const parts = [`${r.added} added`];
      if (r.alreadyInCampaign > 0) parts.push(`${r.alreadyInCampaign} already in it`);
      if (r.notAllowed > 0) parts.push(`${r.notAllowed} left out`);
      toast.success(parts.join(" · "), {
        action: { label: "Open", onClick: () => void navigate({ to: "/campaigns/$campaignId", params: { campaignId } }) },
      });
      onOpenChange(false);
    },
    onError: (err) => toast.error(errorMessage(err, "Could not add the tickets")),
  });

  const campaigns = list.data ?? [];

  return (
    <>
      <Dialog open={open && !creating} onOpenChange={(o) => { if (!add.isPending) onOpenChange(o); }}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <Megaphone className="h-4 w-4 text-primary" /> Add to campaign
            </DialogTitle>
            <DialogDescription>
              {ticketIds.length} selected ticket{ticketIds.length === 1 ? "" : "s"}. Each gets its own mail, sent from
              the ticket itself.
            </DialogDescription>
          </DialogHeader>
          <div className="max-h-72 space-y-1 overflow-y-auto">
            {list.isLoading ? (
              <p className="p-2 text-sm text-muted-foreground">Loading campaigns…</p>
            ) : campaigns.length === 0 ? (
              <p className="p-2 text-sm text-muted-foreground">No current campaigns — start a new one.</p>
            ) : campaigns.map((c) => (
              <button
                key={c.id}
                type="button"
                onClick={() => setPicked(c.id)}
                className={cn(
                  "flex w-full items-center justify-between rounded-md border px-3 py-2 text-left text-sm",
                  picked === c.id ? "border-primary/40 bg-primary/10" : "border-glass hover:bg-glass/60",
                )}
              >
                <span className="truncate font-medium">{c.name}</span>
                <span className="ml-3 shrink-0 text-[11px] text-muted-foreground">{c.ticketCount} tickets</span>
              </button>
            ))}
          </div>
          <DialogFooter className="sm:justify-between">
            <Button variant="ghost" onClick={() => setCreating(true)} disabled={add.isPending}>
              <Plus className="mr-1.5 h-4 w-4" /> New campaign
            </Button>
            <Button onClick={() => picked && add.mutate(picked)} disabled={!picked || add.isPending}>
              {add.isPending && <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />}
              Add tickets
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
      <NewCampaignDialog
        open={open && creating}
        onOpenChange={(o) => { setCreating(o); if (!o) onOpenChange(false); }}
        ticketIds={ticketIds}
      />
    </>
  );
}
