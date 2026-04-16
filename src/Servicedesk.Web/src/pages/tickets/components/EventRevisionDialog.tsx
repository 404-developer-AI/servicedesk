import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import DOMPurify from "dompurify";
import { History } from "lucide-react";
import { useServerTime, toServerLocal, formatUtcSuffix } from "@/hooks/useServerTime";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from "@/components/ui/dialog";
import { ticketApi, type TicketEventRevision } from "@/lib/ticket-api";
import { Skeleton } from "@/components/ui/skeleton";

type EventRevisionDialogProps = {
  ticketId: string;
  eventId: number;
  open: boolean;
  onOpenChange: (open: boolean) => void;
};

function RevisionEntry({
  revision,
  isFirst,
}: {
  revision: TicketEventRevision;
  isFirst: boolean;
}) {
  const { time: serverTime } = useServerTime();
  const offset = serverTime?.offsetMinutes ?? 0;
  const [expanded, setExpanded] = React.useState(isFirst);

  return (
    <div className="glass-panel p-3 space-y-2">
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <span className="text-xs font-medium text-foreground/80">
            Revision {revision.revisionNumber}
          </span>
          {revision.isInternalBefore !== undefined && (
            <span className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-white/10 bg-white/[0.04] text-muted-foreground/60">
              {revision.isInternalBefore ? "Internal" : "Public"}
            </span>
          )}
        </div>
        <span className="text-xs text-muted-foreground shrink-0">
          {toServerLocal(revision.editedUtc, offset)} <span className="text-muted-foreground/40">{formatUtcSuffix(revision.editedUtc)}</span>
        </span>
      </div>

      <div className="text-xs text-muted-foreground">
        Edited by{" "}
        <span className="text-foreground/80">
          {revision.editedByName ?? "Unknown"}
        </span>
      </div>

      {revision.bodyHtmlBefore || revision.bodyTextBefore ? (
        <>
          <button
            type="button"
            onClick={() => setExpanded(!expanded)}
            className="text-xs text-primary/70 hover:text-primary transition-colors"
          >
            {expanded ? "Hide previous content" : "Show previous content"}
          </button>
          {expanded && (
            <div className="rounded-md border border-white/10 bg-white/[0.02] p-3 text-sm">
              {revision.bodyHtmlBefore ? (
                <div
                  className="prose-sm text-foreground/70 [&_a]:text-primary [&_a]:underline [&_p]:my-1 [&_ul]:pl-5 [&_ol]:pl-5"
                  dangerouslySetInnerHTML={{
                    __html: DOMPurify.sanitize(revision.bodyHtmlBefore),
                  }}
                />
              ) : (
                <p className="whitespace-pre-wrap text-foreground/70">
                  {revision.bodyTextBefore}
                </p>
              )}
            </div>
          )}
        </>
      ) : (
        <span className="text-xs text-muted-foreground/50 italic">
          No body content
        </span>
      )}
    </div>
  );
}

export function EventRevisionDialog({
  ticketId,
  eventId,
  open,
  onOpenChange,
}: EventRevisionDialogProps) {
  const { data, isLoading } = useQuery({
    queryKey: ["event-revisions", ticketId, eventId],
    queryFn: () => ticketApi.getEventRevisions(ticketId, eventId),
    enabled: open,
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="glass-card border-white/10 bg-background/95 backdrop-blur-xl max-w-xl max-h-[70vh] flex flex-col">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <History className="h-4 w-4 text-muted-foreground" />
            Edit history
          </DialogTitle>
          <DialogDescription>
            All changes made to this event, newest first.
          </DialogDescription>
        </DialogHeader>

        <div className="flex-1 min-h-0 overflow-y-auto space-y-3 pr-1">
          {isLoading ? (
            <div className="space-y-3">
              <Skeleton className="h-24 w-full" />
              <Skeleton className="h-24 w-full" />
            </div>
          ) : !data || data.length === 0 ? (
            <div className="text-sm text-muted-foreground py-4 text-center">
              No edit history found.
            </div>
          ) : (
            data.map((revision, i) => (
              <RevisionEntry
                key={revision.id}
                revision={revision}
                isFirst={i === 0}
              />
            ))
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
