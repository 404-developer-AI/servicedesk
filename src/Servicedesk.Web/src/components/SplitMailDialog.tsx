import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { GitBranch } from "lucide-react";
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
import { ApiError, ticketApi } from "@/lib/ticket-api";

type Props = {
  open: boolean;
  sourceTicketId: string;
  sourceMailEventId: number;
  mailSubject: string | null;
  onClose: () => void;
};

/// v0.0.23: lifts a single received-mail off the current ticket into a brand
/// new ticket. Queue/priority/status default to the system defaults — the
/// agent fills only the title. The new ticket inherits the requester and
/// company from the source so threading and access stay consistent.
export function SplitMailDialog({
  open,
  sourceTicketId,
  sourceMailEventId,
  mailSubject,
  onClose,
}: Props) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [subject, setSubject] = React.useState("");

  React.useEffect(() => {
    if (open) {
      setSubject("");
    }
  }, [open]);

  const splitMutation = useMutation({
    mutationFn: () =>
      ticketApi.split(sourceTicketId, {
        sourceMailEventId,
        newSubject: subject.trim(),
      }),
    onSuccess: (response) => {
      queryClient.invalidateQueries({ queryKey: ["ticket", sourceTicketId] });
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      toast.success(`Split into #${response.newTicketNumber}`);
      onClose();
      navigate({
        to: "/tickets/$ticketId",
        params: { ticketId: response.newTicketId },
      });
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        toast.error(`Split failed: ${err.message}`);
      } else if (err instanceof Error) {
        toast.error(err.message);
      } else {
        toast.error("Split failed");
      }
    },
  });

  const canSubmit = subject.trim().length > 0 && !splitMutation.isPending;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && !splitMutation.isPending && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <GitBranch className="h-4 w-4 text-primary" />
            Split into a new ticket
          </DialogTitle>
          <DialogDescription>
            Create a new ticket from this received mail. The original ticket
            stays intact and gains a "Split into #..." note.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="rounded-md border border-white/10 bg-white/[0.02] px-3 py-2.5">
            <div className="text-[10px] uppercase tracking-wider text-muted-foreground/70 mb-1">
              Splitting from this mail
            </div>
            <div className="text-sm font-medium text-foreground/90 truncate">
              {mailSubject ?? "(no subject)"}
            </div>
            <div className="text-xs text-muted-foreground/70 mt-1">
              The new ticket will use the same requester and company. Queue,
              priority, and status default to the system defaults so you can
              re-triage explicitly.
            </div>
          </div>

          <div className="space-y-1.5">
            <label
              htmlFor="split-subject"
              className="text-xs font-medium text-foreground/80"
            >
              New ticket title
            </label>
            <Input
              id="split-subject"
              autoFocus
              value={subject}
              onChange={(e) => setSubject(e.target.value)}
              placeholder="Enter a clear, specific title..."
              onKeyDown={(e) => {
                if (e.key === "Enter" && canSubmit) {
                  e.preventDefault();
                  splitMutation.mutate();
                }
              }}
            />
          </div>
        </div>

        <DialogFooter>
          <Button
            variant="ghost"
            onClick={onClose}
            disabled={splitMutation.isPending}
          >
            Cancel
          </Button>
          <Button
            onClick={() => splitMutation.mutate()}
            disabled={!canSubmit}
          >
            {splitMutation.isPending ? "Splitting..." : "Split"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
