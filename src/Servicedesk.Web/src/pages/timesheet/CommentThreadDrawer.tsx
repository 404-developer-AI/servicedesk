import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Check, ExternalLink, Send, Users } from "lucide-react";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  timesheetCommentsApi,
  type BackofficeContext,
  type CommentMessage,
} from "@/lib/api";
import { useAuth } from "@/auth/authStore";
import { openTicketInSharedWindow } from "@/lib/ticketWindow";
import { cn } from "@/lib/utils";

/// What a drawer is anchored to. `threadId` continues an existing thread; when
/// null the drawer starts a fresh one for `ticketNumber` (the review tabs
/// always provide `entityId` — the origin row — for that create path).
export type CommentTarget = {
  threadId: string | null;
  ticketNumber: number;
  ticketId: string | null;
  context: BackofficeContext;
  entityId?: string;
};

const CONTEXT_LABEL: Record<BackofficeContext, string> = {
  adsolut: "Adsolut",
  resolved: "Resolved",
  cwi: "CWI",
};

function formatStamp(iso: string): string {
  try {
    return new Intl.DateTimeFormat(undefined, {
      month: "short",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}

/// Shared chat panel for the Timesheet → Comments feature. Used from the
/// Comments inbox and from the per-row comment button on the Adsolut /
/// Resolved / CWI tabs. Renders the message history and a composer with a
/// mandatory recipient picker (linking ≥1 colleague is required both ways).
export function CommentThreadDrawer({
  open,
  onOpenChange,
  target,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  target: CommentTarget;
}) {
  const qc = useQueryClient();
  const { user } = useAuth();
  const myId = user?.id ?? "";

  // A thread can be created during this drawer's life — adopt the returned id
  // so the history loads and later replies append to the same thread.
  const [threadId, setThreadId] = React.useState<string | null>(target.threadId);
  React.useEffect(() => {
    setThreadId(target.threadId);
  }, [target.threadId, target.ticketNumber, target.context]);

  const [body, setBody] = React.useState("");
  const [recipientIds, setRecipientIds] = React.useState<string[]>([]);
  // Once the user touches the recipient picker we stop auto-prefilling so we
  // never override a deliberate choice.
  const recipientsTouched = React.useRef(false);
  React.useEffect(() => {
    if (!open) {
      setBody("");
      setRecipientIds([]);
      recipientsTouched.current = false;
    }
  }, [open]);

  const thread = useQuery({
    queryKey: ["timesheet", "comments", "thread", threadId],
    queryFn: () => timesheetCommentsApi.thread(threadId as string),
    enabled: open && !!threadId,
  });

  const recipientOptions = useQuery({
    queryKey: ["timesheet", "comments", "recipients"],
    queryFn: () => timesheetCommentsApi.recipients(),
    enabled: open,
    staleTime: 60_000,
  });

  // Mark the thread read on open and whenever new messages arrive while it is
  // open, then refresh the unread surfaces (inbox, nav dot, row dots). Guarded
  // on the highest message id already marked so the post-mark refetch (same
  // max id) can't trigger a re-mark loop.
  const messages = thread.data?.messages;
  const markedUpToRef = React.useRef(0);
  React.useEffect(() => {
    if (!open) {
      markedUpToRef.current = 0;
      return;
    }
    if (!threadId || !messages || messages.length === 0) return;
    const maxId = messages[messages.length - 1].id;
    if (maxId <= markedUpToRef.current) return;
    markedUpToRef.current = maxId;
    timesheetCommentsApi
      .markRead(threadId)
      .then(() => qc.invalidateQueries({ queryKey: ["timesheet"] }))
      .catch(() => {});
  }, [open, threadId, messages, qc]);

  // Prefill the reply recipients with the people on the other side of the
  // conversation (distinct message authors that aren't me, falling back to the
  // thread starter). Only while the user hasn't picked manually.
  React.useEffect(() => {
    if (!open || recipientsTouched.current || !thread.data) return;
    const others = new Set<string>();
    for (const m of thread.data.messages) {
      if (m.authorId && m.authorId !== myId) others.add(m.authorId);
    }
    if (others.size === 0 && thread.data.createdBy && thread.data.createdBy !== myId) {
      others.add(thread.data.createdBy);
    }
    if (others.size > 0) setRecipientIds([...others]);
  }, [open, thread.data, myId]);

  const post = useMutation({
    mutationFn: (vars: { body: string; recipientIds: string[] }) =>
      timesheetCommentsApi.post({
        threadId: threadId ?? undefined,
        ticketNumber: target.ticketNumber,
        ticketId: target.ticketId,
        context: target.context,
        entityId: target.entityId,
        body: vars.body,
        recipientIds: vars.recipientIds,
      }),
    onSuccess: (res) => {
      setThreadId(res.threadId);
      setBody("");
      recipientsTouched.current = false;
      qc.invalidateQueries({ queryKey: ["timesheet"] });
    },
    onError: () => toast.error("Could not send the comment"),
  });

  const canSend = body.trim().length > 0 && recipientIds.length > 0 && !post.isPending;

  const send = () => {
    if (!canSend) return;
    post.mutate({ body: body.trim(), recipientIds });
  };

  const options = recipientOptions.data ?? [];

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent
        side="right"
        className="glass-panel flex w-full flex-col border-glass p-0 sm:max-w-md"
      >
        <SheetHeader className="space-y-1 border-b border-glass px-4 py-3 text-left">
          <SheetTitle className="flex items-center gap-2 text-base">
            <span className="text-foreground">Comments</span>
            <span className="rounded-full border border-glass bg-glass px-2 py-0.5 text-[11px] font-normal text-muted-foreground">
              {CONTEXT_LABEL[target.context]}
            </span>
          </SheetTitle>
          <button
            type="button"
            onClick={() => target.ticketId && openTicketInSharedWindow(target.ticketId)}
            disabled={!target.ticketId}
            className={cn(
              "inline-flex w-fit items-center gap-1 font-mono text-xs",
              target.ticketId
                ? "text-primary hover:underline"
                : "cursor-default text-muted-foreground",
            )}
            title={target.ticketId ? "Open the ticket in a separate window" : undefined}
          >
            #{target.ticketNumber}
            {target.ticketId && <ExternalLink className="h-3 w-3" />}
          </button>
        </SheetHeader>

        {/* Message history */}
        <div className="flex-1 space-y-3 overflow-y-auto px-4 py-4">
          {!threadId ? (
            <EmptyState first />
          ) : thread.isLoading ? (
            <div className="space-y-3">
              <Skeleton className="h-16 w-3/4" />
              <Skeleton className="ml-auto h-16 w-3/4" />
            </div>
          ) : thread.isError ? (
            <p className="text-sm text-rose-300">Could not load this conversation.</p>
          ) : (thread.data?.messages.length ?? 0) === 0 ? (
            <EmptyState first={false} />
          ) : (
            thread.data!.messages.map((m) => (
              <MessageBubble key={m.id} message={m} mine={m.authorId === myId} />
            ))
          )}
        </div>

        {/* Composer */}
        <div className="space-y-2 border-t border-glass px-4 py-3">
          <RecipientPicker
            options={options}
            selected={recipientIds}
            onChange={(ids) => {
              recipientsTouched.current = true;
              setRecipientIds(ids);
            }}
          />
          <div className="flex items-end gap-2">
            <textarea
              value={body}
              onChange={(e) => setBody(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) {
                  e.preventDefault();
                  send();
                }
              }}
              rows={6}
              placeholder="Write a comment…"
              className="min-h-[7.5rem] flex-1 resize-y rounded-lg border border-glass bg-glass px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:border-primary/40 focus:outline-none"
            />
            <Button
              size="sm"
              onClick={send}
              disabled={!canSend}
              className="h-10 shrink-0 gap-1.5"
              title={recipientIds.length === 0 ? "Link at least one person" : "Send (⌘/Ctrl+Enter)"}
            >
              <Send className="h-4 w-4" />
            </Button>
          </div>
          {recipientIds.length === 0 && (
            <p className="text-[11px] text-amber-600 dark:text-amber-300/80">
              Link at least one person before sending.
            </p>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}

function EmptyState({ first }: { first: boolean }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-2 py-10 text-center">
      <Users className="h-6 w-6 text-muted-foreground/60" />
      <p className="max-w-[16rem] text-sm text-muted-foreground">
        {first
          ? "No comments yet. Write one and link the colleague(s) it concerns."
          : "No messages in this conversation yet."}
      </p>
    </div>
  );
}

function MessageBubble({ message, mine }: { message: CommentMessage; mine: boolean }) {
  const who = message.authorEmail?.split("@")[0] ?? "unknown";
  return (
    <div className={cn("flex flex-col gap-1", mine ? "items-end" : "items-start")}>
      <div
        className={cn(
          "max-w-[85%] whitespace-pre-wrap break-words rounded-2xl px-3 py-2 text-sm",
          mine
            ? "rounded-br-sm bg-primary/15 text-foreground"
            : "rounded-bl-sm border border-glass bg-glass text-foreground",
        )}
      >
        {message.body}
      </div>
      <div className="flex items-center gap-1.5 px-1 text-[10px] text-muted-foreground/70">
        <span>{mine ? "You" : who}</span>
        <span>·</span>
        <span>{formatStamp(message.createdUtc)}</span>
        {message.recipients.length > 0 && (
          <>
            <span>·</span>
            <span className="inline-flex items-center gap-0.5">
              <Users className="h-2.5 w-2.5" />
              {message.recipients.map((r) => r.email.split("@")[0]).join(", ")}
            </span>
          </>
        )}
      </div>
    </div>
  );
}

function RecipientPicker({
  options,
  selected,
  onChange,
}: {
  options: { id: string; email: string }[];
  selected: string[];
  onChange: (ids: string[]) => void;
}) {
  const [open, setOpen] = React.useState(false);
  const selectedSet = new Set(selected);
  const toggle = (id: string) => {
    const next = new Set(selectedSet);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    onChange([...next]);
  };
  const labelFor = (id: string) => options.find((o) => o.id === id)?.email.split("@")[0] ?? "…";

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button
            size="sm"
            variant="ghost"
            className="h-7 gap-1.5 border border-glass px-2 text-xs"
          >
            <Users className="h-3.5 w-3.5" />
            {selected.length === 0 ? "Link people" : `Linked (${selected.length})`}
          </Button>
        </PopoverTrigger>
        <PopoverContent align="start" className="w-60 p-1.5">
          <div className="px-1 pb-1.5 text-[11px] uppercase tracking-wider text-muted-foreground/70">
            Link to (required)
          </div>
          <div className="max-h-56 space-y-0.5 overflow-auto">
            {options.length === 0 ? (
              <p className="px-1.5 py-2 text-xs text-muted-foreground">No timesheet users.</p>
            ) : (
              options.map((o) => {
                const on = selectedSet.has(o.id);
                return (
                  <button
                    key={o.id}
                    type="button"
                    onClick={() => toggle(o.id)}
                    className="flex w-full items-center justify-between gap-2 rounded-md px-1.5 py-1 text-left text-sm hover:bg-glass-hover"
                  >
                    <span className={cn("truncate", on ? "text-foreground" : "text-muted-foreground")}>
                      {o.email}
                    </span>
                    {on && <Check className="h-3.5 w-3.5 shrink-0 text-primary" />}
                  </button>
                );
              })
            )}
          </div>
        </PopoverContent>
      </Popover>
      {selected.map((id) => (
        <span
          key={id}
          className="inline-flex items-center gap-1 rounded-full border border-primary/30 bg-primary/10 px-2 py-0.5 text-[11px] text-primary"
        >
          {labelFor(id)}
          <button
            type="button"
            onClick={() => toggle(id)}
            className="text-primary/70 hover:text-primary"
            aria-label="Remove"
          >
            ×
          </button>
        </span>
      ))}
    </div>
  );
}
