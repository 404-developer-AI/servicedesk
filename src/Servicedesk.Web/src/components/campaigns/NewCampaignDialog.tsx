import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Loader2, Megaphone } from "lucide-react";
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
import { Input } from "@/components/ui/input";
import { errorMessage } from "@/lib/http";
import { CAMPAIGNS_KEY, campaignsApi } from "@/lib/campaigns-api";

/// v0.1.67 — start a campaign: from the Campaigns page (empty), from a
/// ticket-list selection (`ticketIds`) or from a project ticket
/// (`projectTicketId` — its open linked tickets become the recipients).
/// Opens the new campaign so the agent writes the message next.
export function NewCampaignDialog({
  open,
  onOpenChange,
  ticketIds,
  projectTicketId,
  defaultName = "",
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  ticketIds?: string[];
  projectTicketId?: string | null;
  defaultName?: string;
}) {
  const [name, setName] = React.useState(defaultName);
  React.useEffect(() => { if (open) setName(defaultName); }, [open, defaultName]);
  const navigate = useNavigate();
  const qc = useQueryClient();

  const create = useMutation({
    mutationFn: () => campaignsApi.create({
      name: name.trim(),
      ticketIds: ticketIds && ticketIds.length > 0 ? ticketIds : undefined,
      projectTicketId: projectTicketId ?? null,
      addProjectTickets: !!projectTicketId,
    }),
    onSuccess: (res) => {
      void qc.invalidateQueries({ queryKey: CAMPAIGNS_KEY });
      const added = res.added;
      if (added && added.notAllowed > 0) {
        toast.message(`${added.added} ticket${added.added === 1 ? "" : "s"} added`, {
          description: `${added.notAllowed} left out: no queue access, a project ticket, or merged.`,
        });
      }
      onOpenChange(false);
      void navigate({ to: "/campaigns/$campaignId", params: { campaignId: res.id } });
    },
    onError: (err) => toast.error(errorMessage(err, "Could not create the campaign")),
  });

  const count = ticketIds?.length ?? 0;
  const description = projectTicketId
    ? "The project's open linked tickets become the recipients. You write the message next."
    : count > 0
      ? `The ${count} selected ticket${count === 1 ? "" : "s"} become the recipients. You write the message next.`
      : "Give it a name. You add tickets and write the message next.";

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!create.isPending) onOpenChange(o); }}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Megaphone className="h-4 w-4 text-primary" /> New campaign
          </DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <form
          id="sd-new-campaign"
          onSubmit={(e) => { e.preventDefault(); if (name.trim()) create.mutate(); }}
          className="space-y-1.5"
        >
          <label htmlFor="sd-campaign-name" className="text-xs font-medium text-muted-foreground">Name</label>
          <Input
            id="sd-campaign-name"
            autoFocus
            value={name}
            maxLength={200}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. Firewall updates — spring"
          />
        </form>
        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)} disabled={create.isPending}>Cancel</Button>
          <Button type="submit" form="sd-new-campaign" disabled={!name.trim() || create.isPending}>
            {create.isPending && <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />}
            Create campaign
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
