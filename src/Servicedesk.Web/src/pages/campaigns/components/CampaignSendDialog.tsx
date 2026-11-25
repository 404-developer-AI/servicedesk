import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Loader2, Send } from "lucide-react";
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
import { PendingTillField } from "@/components/PendingTillField";
import { TaxonomySelect, type TaxonomyOption } from "@/components/TaxonomySelect";
import { taxonomyApi } from "@/lib/taxonomy-api";
import { errorMessage } from "@/lib/http";
import {
  campaignKeys,
  campaignsApi,
  skipReasonLabel,
  type CampaignRoundKind,
  type CampaignSettings,
} from "@/lib/campaigns-api";

/// v0.1.67 — send a round. The dry run lists who gets a mail and why the
/// others are skipped (never forced). Optionally every mailed ticket moves to
/// a Pending status with a "pending till" right after its own mail went out,
/// through the normal ticket rules — so the pending timer (trigger) reopens
/// tickets that never answer. Mails leave one by one at the configured pace.
export function CampaignSendDialog({
  campaignId,
  kind,
  open,
  onOpenChange,
  settings,
}: {
  campaignId: string;
  kind: CampaignRoundKind;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  settings: CampaignSettings | undefined;
}) {
  const qc = useQueryClient();
  const [statusId, setStatusId] = React.useState<string | null>(null);
  const [pendingTill, setPendingTill] = React.useState<string | null>(null);
  const [scheduled, setScheduled] = React.useState(false);
  const [sendAt, setSendAt] = React.useState<string | null>(null);
  React.useEffect(() => {
    if (open) { setStatusId(null); setPendingTill(null); setScheduled(false); setSendAt(null); }
  }, [open]);

  const plan = useQuery({
    queryKey: campaignKeys.plan(campaignId, kind),
    queryFn: () => campaignsApi.plan(campaignId, kind),
    enabled: open,
    staleTime: 0,
  });
  const statuses = useQuery({ queryKey: ["statuses"], queryFn: taxonomyApi.statuses.list, staleTime: 300_000, enabled: open });
  const pendingOptions: TaxonomyOption[] = React.useMemo(
    () => (statuses.data ?? [])
      .filter((s) => s.isActive && s.stateCategory === "Pending")
      .map((s) => ({ id: s.id, name: s.name, color: s.color, badge: s.stateCategory })),
    [statuses.data],
  );

  const send = useMutation({
    mutationFn: () => campaignsApi.startRound(campaignId, {
      kind,
      pendingStatusId: statusId || null,
      pendingTillUtc: statusId ? pendingTill : null,
      scheduledUtc: scheduled ? sendAt : null,
    }),
    onSuccess: (r) => {
      toast.success(scheduled
        ? `${r.queued} mail${r.queued === 1 ? "" : "s"} scheduled`
        : `${r.queued} mail${r.queued === 1 ? "" : "s"} queued`, {
        description: r.skipped > 0
          ? `${r.skipped} skipped — see the list for the reason.`
          : scheduled
            ? "They go out at the chosen moment; you can cancel until then."
            : "They are sent one by one; the list updates as they go.",
      });
      void qc.invalidateQueries({ queryKey: campaignKeys.detail(campaignId) });
      onOpenChange(false);
    },
    onError: (err) => toast.error(errorMessage(err, "Could not send the campaign")),
  });

  const items = plan.data ?? [];
  const sendable = items.filter((i) => !i.skipReason);
  const skipped = items.filter((i) => i.skipReason);
  const minutes = settings && sendable.length > 0 ? Math.ceil(sendable.length / Math.max(1, settings.sendRatePerMinute)) : 0;

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!send.isPending) onOpenChange(o); }}>
      <DialogContent className="flex max-h-[90vh] w-[calc(100vw-2rem)] max-w-2xl flex-col">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Send className="h-4 w-4 text-primary" />
            {kind === "FollowUp" ? "Send a follow-up" : "Send campaign"}
          </DialogTitle>
          <DialogDescription>
            {kind === "FollowUp"
              ? "Goes only to tickets that were mailed, did not answer and have no decision yet."
              : "Goes to every ticket in this campaign that was not mailed yet."}{" "}
            Each mail is sent from its own ticket as a reply to the customer's latest mail.
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-5 overflow-y-auto pr-1">
          {plan.isLoading ? (
            <p className="text-sm text-muted-foreground">Checking the tickets…</p>
          ) : plan.isError ? (
            <p className="text-sm text-destructive">{errorMessage(plan.error, "Could not check the tickets")}</p>
          ) : (
            <div className="space-y-2">
              <p className="text-sm">
                <span className="font-semibold tabular-nums">{sendable.length}</span> ticket{sendable.length === 1 ? "" : "s"} will get a mail
                {minutes > 0 && <span className="text-muted-foreground"> · about {minutes} min to send all</span>}.
              </p>
              {skipped.length > 0 && (
                <div className="rounded-md border border-amber-400/45 bg-amber-100/70 px-3 py-2 text-amber-900 dark:bg-amber-400/12 dark:text-amber-200">
                  <div className="flex items-center gap-2 text-sm font-medium">
                    <AlertTriangle className="h-4 w-4" /> {skipped.length} will be skipped
                  </div>
                  <ul className="mt-1.5 max-h-40 space-y-0.5 overflow-y-auto text-xs">
                    {skipped.map((s) => (
                      <li key={s.ticketId}>
                        #{s.number} {s.ticketSubject} — {skipReasonLabel(s.skipReason)}
                        {s.missingTokens.length > 0 && ` (${s.missingTokens.join(", ")})`}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          )}

          <div className="space-y-3 rounded-md border border-glass px-3 py-3">
            <div>
              <div className="text-sm font-medium">When</div>
              <p className="text-[11px] text-muted-foreground">
                A scheduled round goes out at the chosen moment (server time) and can be cancelled until then.
                Every rule is checked again when the mail leaves.
              </p>
            </div>
            <div className="flex flex-wrap items-center gap-3 text-sm">
              <label className="inline-flex items-center gap-1.5">
                <input type="radio" name="sd-campaign-when" checked={!scheduled} onChange={() => setScheduled(false)} /> Send now
              </label>
              <label className="inline-flex items-center gap-1.5">
                <input type="radio" name="sd-campaign-when" checked={scheduled} onChange={() => setScheduled(true)} /> Send on…
              </label>
              {scheduled && (
                <div className="w-60">
                  <PendingTillField value={sendAt} onCommit={setSendAt} commitOnChange pastMessage="Pick a send moment in the future." />
                </div>
              )}
            </div>
          </div>

          <div className="space-y-3 rounded-md border border-glass px-3 py-3">
            <div>
              <div className="text-sm font-medium">After the mail: wait for the customer</div>
              <p className="text-[11px] text-muted-foreground">
                Optional. Sets each mailed ticket to a Pending status right after its mail went out. With a “pending
                till”, your reminder trigger reopens tickets that never answered — they show as “Timer expired” here.
              </p>
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              <TaxonomySelect
                value={statusId ?? ""}
                onChange={setStatusId}
                options={pendingOptions}
                allowEmpty
                emptyLabel="Leave the status as it is"
                placeholder="Leave the status as it is"
                disabled={send.isPending}
              />
              {statusId && (
                <PendingTillField value={pendingTill} onCommit={setPendingTill} commitOnChange />
              )}
            </div>
          </div>
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)} disabled={send.isPending}>Cancel</Button>
          <Button
            onClick={() => send.mutate()}
            disabled={send.isPending || plan.isLoading || sendable.length === 0 || (scheduled && !sendAt)}
          >
            {send.isPending ? <Loader2 className="mr-1.5 h-4 w-4 animate-spin" /> : <Send className="mr-1.5 h-4 w-4" />}
            {scheduled ? "Schedule" : "Send"} {sendable.length} mail{sendable.length === 1 ? "" : "s"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
