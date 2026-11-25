import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, ChevronLeft, ChevronRight, Eye } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { SafeHtml } from "@/components/SafeHtml";
import { Skeleton } from "@/components/ui/skeleton";
import { campaignKeys, campaignsApi, skipReasonLabel } from "@/lib/campaigns-api";

/// v0.1.67 — the mail exactly as one recipient gets it (rendered on the
/// server with that ticket's contact, company and proposed date), plus why
/// it would be skipped. Browse through the recipients with the arrows.
export function CampaignPreviewDialog({
  campaignId,
  ticketIds,
  ticketId,
  onTicketChange,
  onClose,
}: {
  campaignId: string;
  ticketIds: string[];
  ticketId: string | null;
  onTicketChange: (ticketId: string) => void;
  onClose: () => void;
}) {
  const preview = useQuery({
    queryKey: campaignKeys.preview(campaignId, ticketId ?? ""),
    queryFn: () => campaignsApi.preview(campaignId, ticketId!),
    enabled: !!ticketId,
    staleTime: 10_000,
  });
  const index = ticketId ? ticketIds.indexOf(ticketId) : -1;
  const go = (delta: number) => {
    if (ticketIds.length === 0) return;
    const next = (index + delta + ticketIds.length) % ticketIds.length;
    onTicketChange(ticketIds[next]);
  };
  const p = preview.data;

  return (
    <Dialog open={!!ticketId} onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="flex max-h-[90vh] w-[calc(100vw-2rem)] max-w-3xl flex-col">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Eye className="h-4 w-4 text-primary" /> Preview
          </DialogTitle>
          <DialogDescription>
            {p ? `#${p.number} — ${p.requesterName ?? "No contact"} <${p.requesterEmail ?? "no e-mail address"}>` : "Rendering…"}
          </DialogDescription>
        </DialogHeader>

        <div className="flex items-center justify-between gap-2">
          <Button size="sm" variant="ghost" onClick={() => go(-1)} disabled={ticketIds.length < 2}>
            <ChevronLeft className="mr-1 h-4 w-4" /> Previous
          </Button>
          <span className="text-xs tabular-nums text-muted-foreground">
            {index >= 0 ? `${index + 1} of ${ticketIds.length}` : ""}
          </span>
          <Button size="sm" variant="ghost" onClick={() => go(1)} disabled={ticketIds.length < 2}>
            Next <ChevronRight className="ml-1 h-4 w-4" />
          </Button>
        </div>

        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1">
          {preview.isLoading || !p ? (
            <div className="space-y-2">
              <Skeleton className="h-6 w-2/3" />
              <Skeleton className="h-40 w-full" />
            </div>
          ) : (
            <>
              {p.skipReason && (
                <div className="flex items-start gap-2 rounded-md border border-amber-400/45 bg-amber-100/70 px-3 py-2 text-sm text-amber-900 dark:bg-amber-400/12 dark:text-amber-200">
                  <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
                  <div>
                    <div className="font-medium">This ticket would be skipped: {skipReasonLabel(p.skipReason)}</div>
                    {p.missingTokens.length > 0 && (
                      <div className="mt-0.5 text-xs">Empty for this ticket: {p.missingTokens.join(", ")}</div>
                    )}
                  </div>
                </div>
              )}
              <div className="rounded-md border border-glass px-3 py-2">
                <div className="text-[11px] uppercase tracking-wider text-muted-foreground/70">Subject</div>
                <div className="text-sm font-medium">{p.renderedSubject || "—"}</div>
              </div>
              <div className="rounded-md border border-glass bg-glass/40 px-4 py-3">
                <SafeHtml html={p.renderedBodyHtml ?? ""} className="prose-sm max-w-none text-sm" />
              </div>
              <p className="text-[11px] text-muted-foreground/70">
                The ticket reference is added to the subject and your signature to the mail when it is sent.
              </p>
            </>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
