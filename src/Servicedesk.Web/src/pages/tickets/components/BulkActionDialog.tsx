import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import { AlertTriangle, CheckCircle2, Layers, Loader2 } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { RichTextEditor } from "@/components/RichTextEditor";
import { TaxonomySelect, type TaxonomyOption } from "@/components/TaxonomySelect";
import { agentQueueApi, taxonomyApi } from "@/lib/api";
import {
  ticketApi,
  userApi,
  type BulkSkippedTicket,
  type BulkTicketActionRequest,
  type BulkTicketActionResult,
  type TicketListItem,
} from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

/// Sentinel option id for "clear the assignee" — never collides with a user id.
const UNASSIGN = "__unassign__";

const SKIP_REASON_LABEL: Record<string, string> = {
  not_found: "Ticket no longer exists",
  no_access: "No access to the ticket's queue",
  target_queue_no_access: "No access to the target queue",
  status_not_in_queue_scope: "Status not allowed in the ticket's queue",
  status_gate_required: "Status change needs a confirmation — open the ticket",
  checklist_incomplete: "Checklist not finished — open the ticket to complete it",
  failed: "Failed — try again or open the ticket",
};

/// Strip tags to decide whether the composer holds any real content: an
/// empty Tiptap document still serialises as `<p></p>`.
function hasText(html: string): boolean {
  const div = document.createElement("div");
  div.innerHTML = html;
  return (div.textContent ?? "").trim().length > 0 || /<img\b/i.test(html);
}

type BulkActionDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /// The selected tickets, in list order. Used for the count, the status
  /// scoping (single common queue) and the result links.
  selected: TicketListItem[];
  /// Called after a successful run — the page clears the selection.
  onCompleted: (result: BulkTicketActionResult) => void;
};

/// v0.0.102 — one dialog for a bulk action over the current selection.
/// Every field starts at "No change"; the message block is optional. The
/// same dialog flips into a result view once the server answers, so the
/// agent sees exactly which tickets were updated and which were skipped
/// (and why) without hunting through toasts.
export function BulkActionDialog({ open, onOpenChange, selected, onCompleted }: BulkActionDialogProps) {
  const qc = useQueryClient();
  const [statusId, setStatusId] = React.useState("");
  const [queueId, setQueueId] = React.useState("");
  const [priorityId, setPriorityId] = React.useState("");
  const [assignee, setAssignee] = React.useState("");
  const [messageInternal, setMessageInternal] = React.useState(true);
  const [messageHtml, setMessageHtml] = React.useState("");
  const [editorKey, setEditorKey] = React.useState(0);
  const [result, setResult] = React.useState<BulkTicketActionResult | null>(null);

  // Reset the form each time the dialog opens so a previous run never leaks
  // a stale status/queue into the next selection.
  React.useEffect(() => {
    if (!open) return;
    setStatusId("");
    setQueueId("");
    setPriorityId("");
    setAssignee("");
    setMessageInternal(true);
    setMessageHtml("");
    setEditorKey((k) => k + 1);
    setResult(null);
  }, [open]);

  const { data: queues } = useQuery({
    queryKey: ["accessible-queues"],
    queryFn: agentQueueApi.list,
    staleTime: 60_000,
    enabled: open,
  });
  const { data: statuses } = useQuery({
    queryKey: ["statuses"],
    queryFn: taxonomyApi.statuses.list,
    staleTime: 300_000,
    enabled: open,
  });
  const { data: priorities } = useQuery({
    queryKey: ["priorities"],
    queryFn: taxonomyApi.priorities.list,
    staleTime: 300_000,
    enabled: open,
  });
  const { data: agents } = useQuery({
    queryKey: ["agents"],
    queryFn: userApi.listAgents,
    staleTime: 60_000,
    enabled: open,
  });

  // Status scoping: when the dialog moves the tickets to a queue, offer that
  // queue's allowed statuses; otherwise, when every selected ticket sits in
  // one queue, scope to that queue. Mixed queues → all active statuses (the
  // server validates per ticket and reports the misfits).
  const commonQueueId = React.useMemo(() => {
    const ids = new Set(selected.map((t) => t.queueId));
    return ids.size === 1 ? selected[0]?.queueId ?? null : null;
  }, [selected]);
  const scopeQueue = React.useMemo(() => {
    const target = queueId || commonQueueId;
    return target ? queues?.find((q) => q.id === target) ?? null : null;
  }, [queueId, commonQueueId, queues]);

  const statusOptions: TaxonomyOption[] = React.useMemo(() => {
    const allowed = scopeQueue?.allowedStatusIds ?? [];
    return (statuses ?? [])
      .filter((s) => s.isActive && (allowed.length === 0 || allowed.includes(s.id)))
      .map((s) => ({ id: s.id, name: s.name, color: s.color, badge: s.stateCategory }));
  }, [statuses, scopeQueue]);
  const queueOptions: TaxonomyOption[] = React.useMemo(
    () => (queues ?? []).filter((q) => q.isActive).map((q) => ({ id: q.id, name: q.name, color: q.color })),
    [queues],
  );
  const priorityOptions: TaxonomyOption[] = React.useMemo(
    () => (priorities ?? []).filter((p) => p.isActive).map((p) => ({ id: p.id, name: p.name, color: p.color })),
    [priorities],
  );
  const assigneeOptions: TaxonomyOption[] = React.useMemo(
    () => [
      { id: UNASSIGN, name: "Unassigned", color: "#6b7280" },
      ...(agents ?? []).map((a) => ({ id: a.id, name: a.email, color: "#8b5cf6" })),
    ],
    [agents],
  );

  // A picked status that fell out of scope after a queue change is dropped
  // silently — the select would otherwise show a value the server rejects.
  React.useEffect(() => {
    if (statusId && !statusOptions.some((o) => o.id === statusId)) setStatusId("");
  }, [statusId, statusOptions]);

  const messageFilled = hasText(messageHtml);
  const hasFieldChange = !!statusId || !!queueId || !!priorityId || !!assignee;
  const hasAnyChange = hasFieldChange || messageFilled;

  const summary: string[] = [];
  if (statusId) summary.push(`Status → ${statusOptions.find((o) => o.id === statusId)?.name ?? "…"}`);
  if (queueId) summary.push(`Queue → ${queueOptions.find((o) => o.id === queueId)?.name ?? "…"}`);
  if (priorityId) summary.push(`Priority → ${priorityOptions.find((o) => o.id === priorityId)?.name ?? "…"}`);
  if (assignee) summary.push(assignee === UNASSIGN ? "Unassign" : `Assign → ${assigneeOptions.find((o) => o.id === assignee)?.name ?? "…"}`);
  if (messageFilled) summary.push(messageInternal ? "+ internal note" : "+ public comment");

  const run = useMutation({
    mutationFn: () => {
      const payload: BulkTicketActionRequest = {
        ticketIds: selected.map((t) => t.id),
        messageIsInternal: messageInternal,
      };
      if (messageFilled) payload.messageHtml = messageHtml;
      if (statusId) payload.statusId = statusId;
      if (queueId) payload.queueId = queueId;
      if (priorityId) payload.priorityId = priorityId;
      if (assignee === UNASSIGN) payload.unassignAssignee = true;
      else if (assignee) payload.assigneeUserId = assignee;
      return ticketApi.bulkAction(payload);
    },
    onSuccess: (res) => {
      setResult(res);
      qc.invalidateQueries({ queryKey: ["tickets"] });
      if (res.skipped.length === 0) toast.success(`${res.succeeded} ticket${res.succeeded === 1 ? "" : "s"} updated`);
      else toast.warning(`${res.succeeded} updated, ${res.skipped.length} skipped`);
      onCompleted(res);
    },
    onError: (err) => {
      const msg = err instanceof Error ? err.message : "Bulk action failed";
      toast.error(msg);
    },
  });

  const count = selected.length;

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!run.isPending) onOpenChange(o); }}>
      <DialogContent className="flex max-h-[90vh] w-[calc(100vw-2rem)] max-w-2xl flex-col">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Layers className="h-4 w-4 text-primary" />
            {result ? "Bulk edit — result" : "Bulk edit"}
          </DialogTitle>
          <DialogDescription>
            {result
              ? `${result.succeeded} of ${result.total} ticket${result.total === 1 ? "" : "s"} updated.`
              : `Applies to ${count} selected ticket${count === 1 ? "" : "s"}. Fields left at “No change” are untouched; every ticket still runs its normal rules and is skipped (not failed) when one doesn't allow the change.`}
          </DialogDescription>
        </DialogHeader>

        {result ? (
          <ResultView result={result} />
        ) : (
          <div className="flex-1 min-h-0 overflow-y-auto pr-1 space-y-5">
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <Field label="Status">
                <TaxonomySelect
                  value={statusId}
                  onChange={setStatusId}
                  options={statusOptions}
                  allowEmpty
                  emptyLabel="No change"
                  placeholder="No change"
                  disabled={run.isPending}
                />
              </Field>
              <Field label="Queue">
                <TaxonomySelect
                  value={queueId}
                  onChange={setQueueId}
                  options={queueOptions}
                  allowEmpty
                  emptyLabel="No change"
                  placeholder="No change"
                  disabled={run.isPending}
                />
              </Field>
              <Field label="Priority">
                <TaxonomySelect
                  value={priorityId}
                  onChange={setPriorityId}
                  options={priorityOptions}
                  allowEmpty
                  emptyLabel="No change"
                  placeholder="No change"
                  disabled={run.isPending}
                />
              </Field>
              <Field label="Assignee">
                <TaxonomySelect
                  value={assignee}
                  onChange={setAssignee}
                  options={assigneeOptions}
                  allowEmpty
                  emptyLabel="No change"
                  placeholder="No change"
                  disabled={run.isPending}
                />
              </Field>
            </div>

            <div
              className={cn(
                "glass-panel p-3 transition-shadow",
                messageFilled && (messageInternal ? "ring-1 ring-amber-500/30" : "ring-1 ring-emerald-500/30"),
              )}
            >
              <div className="mb-2 flex items-center gap-1">
                <button
                  type="button"
                  onClick={() => setMessageInternal(true)}
                  className={cn(
                    "px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
                    messageInternal
                      ? "bg-amber-500/15 text-amber-300 border border-amber-500/30"
                      : "text-muted-foreground hover:text-foreground hover:bg-glass-hover",
                  )}
                >
                  Internal note
                </button>
                <button
                  type="button"
                  onClick={() => setMessageInternal(false)}
                  className={cn(
                    "px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
                    !messageInternal
                      ? "bg-emerald-500/15 text-emerald-300 border border-emerald-500/30"
                      : "text-muted-foreground hover:text-foreground hover:bg-glass-hover",
                  )}
                >
                  Public comment
                </button>
                <span className="ml-auto text-[11px] text-muted-foreground/60">
                  Optional · no mail is sent
                </span>
              </div>
              <RichTextEditor
                key={editorKey}
                content={undefined}
                onChange={setMessageHtml}
                placeholder={
                  messageInternal
                    ? "Add the same internal note to every selected ticket…"
                    : "Add the same public comment to every selected ticket…"
                }
                minHeight="110px"
                maxHeight="40vh"
                editable={!run.isPending}
              />
            </div>
          </div>
        )}

        <DialogFooter className="items-center gap-2 sm:justify-between">
          {result ? (
            <>
              <span />
              <Button onClick={() => onOpenChange(false)}>Done</Button>
            </>
          ) : (
            <>
              <div className="flex min-w-0 flex-wrap items-center gap-1.5">
                {summary.length === 0 ? (
                  <span className="text-xs text-muted-foreground/60">Nothing selected to change yet.</span>
                ) : (
                  summary.map((s) => (
                    <span
                      key={s}
                      className="rounded-md border border-primary/30 bg-primary/10 px-2 py-0.5 text-[11px] font-medium text-primary"
                    >
                      {s}
                    </span>
                  ))
                )}
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <Button variant="ghost" onClick={() => onOpenChange(false)} disabled={run.isPending}>
                  Cancel
                </Button>
                <Button
                  onClick={() => run.mutate()}
                  disabled={!hasAnyChange || count === 0 || run.isPending}
                  className="gap-1.5"
                >
                  {run.isPending && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                  {run.isPending ? "Applying…" : `Apply to ${count} ticket${count === 1 ? "" : "s"}`}
                </Button>
              </div>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1.5">
      <label className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{label}</label>
      {children}
    </div>
  );
}

function ResultView({ result }: { result: BulkTicketActionResult }) {
  const byReason = React.useMemo(() => {
    const map = new Map<string, BulkSkippedTicket[]>();
    for (const s of result.skipped) {
      const list = map.get(s.reason) ?? [];
      list.push(s);
      map.set(s.reason, list);
    }
    return Array.from(map.entries());
  }, [result]);

  return (
    <div className="flex-1 min-h-0 overflow-y-auto pr-1 space-y-4">
      <div className="grid grid-cols-2 gap-3">
        <div className="glass-panel flex items-center gap-3 p-3">
          <CheckCircle2 className="h-5 w-5 text-emerald-400" />
          <div>
            <div className="text-lg font-semibold leading-tight text-foreground">{result.succeeded}</div>
            <div className="text-[11px] uppercase tracking-wider text-muted-foreground">updated</div>
          </div>
        </div>
        <div className={cn("glass-panel flex items-center gap-3 p-3", result.skipped.length > 0 && "ring-1 ring-amber-500/30")}>
          <AlertTriangle className={cn("h-5 w-5", result.skipped.length > 0 ? "text-amber-400" : "text-muted-foreground/40")} />
          <div>
            <div className="text-lg font-semibold leading-tight text-foreground">{result.skipped.length}</div>
            <div className="text-[11px] uppercase tracking-wider text-muted-foreground">skipped</div>
          </div>
        </div>
      </div>

      {byReason.length > 0 && (
        <div className="space-y-3">
          {byReason.map(([reason, items]) => (
            <div key={reason} className="glass-panel p-3">
              <div className="mb-2 text-xs font-medium text-foreground/80">
                {SKIP_REASON_LABEL[reason] ?? reason}
                <span className="ml-1.5 text-muted-foreground/60">({items.length})</span>
              </div>
              <div className="flex flex-wrap gap-1.5">
                {items.map((s) => (
                  <Link
                    key={s.ticketId}
                    to={"/tickets/$id" as never}
                    params={{ id: s.ticketId } as never}
                    className="rounded-md border border-glass bg-glass px-2 py-0.5 font-mono text-[11px] text-primary transition-colors hover:bg-glass-hover"
                  >
                    {s.number != null ? `#${s.number}` : s.ticketId.slice(0, 8)}
                  </Link>
                ))}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
