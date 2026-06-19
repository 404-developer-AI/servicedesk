import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ExternalLink, Inbox, MessageSquare } from "lucide-react";
import { timesheetCommentsApi, type BackofficeContext, type CommentInboxItem } from "@/lib/api";
import { Skeleton } from "@/components/ui/skeleton";
import { openTicketInSharedWindow } from "@/lib/ticketWindow";
import { cn } from "@/lib/utils";
import { CommentThreadDrawer, type CommentTarget } from "@/pages/timesheet/CommentThreadDrawer";

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

/// Timesheet → Comments tab. Lists the conversations the current user is
/// linked into and that are still active (their origin row is not yet
/// BO-checked). Each row opens the shared chat drawer; the ticket number is a
/// clickable control that opens the ticket in a separate window.
export function TimesheetTabComments() {
  const inbox = useQuery({
    queryKey: ["timesheet", "comments", "inbox"],
    queryFn: () => timesheetCommentsApi.inbox(),
  });

  const [target, setTarget] = React.useState<CommentTarget | null>(null);

  const openThread = (item: CommentInboxItem) =>
    setTarget({
      threadId: item.threadId,
      ticketNumber: item.ticketNumber,
      ticketId: item.ticketId,
      context: item.originContext,
    });

  const items = inbox.data ?? [];

  return (
    <div className="flex flex-1 flex-col gap-4">
      <div className="glass-panel flex-1 overflow-hidden">
        {inbox.isLoading ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-14 w-full" />
            <Skeleton className="h-14 w-full" />
            <Skeleton className="h-14 w-full" />
          </div>
        ) : inbox.isError ? (
          <div className="p-6 text-sm text-rose-300">Could not load comments. Refresh and try again.</div>
        ) : items.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
            <Inbox className="h-6 w-6 text-muted-foreground" />
            <p className="max-w-md text-sm text-muted-foreground">
              No comments for you. When a reviewer links you on a Resolved, CWI or
              Adsolut line, the conversation shows up here.
            </p>
          </div>
        ) : (
          <ul className="divide-y divide-glass">
            {items.map((item) => (
              <li key={item.threadId}>
                <button
                  type="button"
                  onClick={() => openThread(item)}
                  className="flex w-full items-center gap-3 px-4 py-3 text-left transition-colors hover:bg-glass-hover"
                >
                  {/* unread dot */}
                  <span className="flex w-2 shrink-0 justify-center">
                    {item.unreadCount > 0 && (
                      <span className="h-2 w-2 rounded-full bg-red-500 shadow-[0_0_0_3px_hsl(var(--background))]" />
                    )}
                  </span>

                  <MessageSquare
                    className={cn(
                      "h-4 w-4 shrink-0",
                      item.unreadCount > 0 ? "text-primary" : "text-muted-foreground/60",
                    )}
                  />

                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span
                        onClick={(e) => {
                          e.stopPropagation();
                          if (item.ticketId) openTicketInSharedWindow(item.ticketId);
                        }}
                        className={cn(
                          "inline-flex items-center gap-1 font-mono text-xs",
                          item.ticketId
                            ? "text-primary hover:underline"
                            : "text-muted-foreground",
                        )}
                        title={item.ticketId ? "Open the ticket in a separate window" : undefined}
                      >
                        #{item.ticketNumber}
                        {item.ticketId && <ExternalLink className="h-3 w-3" />}
                      </span>
                      <span className="rounded-full border border-glass bg-glass px-1.5 py-0.5 text-[10px] text-muted-foreground">
                        {CONTEXT_LABEL[item.originContext]}
                      </span>
                      {item.unreadCount > 0 && (
                        <span className="rounded-full bg-red-500/15 px-1.5 py-0.5 text-[10px] font-medium text-red-300">
                          {item.unreadCount} new
                        </span>
                      )}
                    </div>
                    <p
                      className={cn(
                        "mt-0.5 truncate text-sm",
                        item.unreadCount > 0 ? "text-foreground" : "text-muted-foreground",
                      )}
                    >
                      {item.lastAuthorEmail && (
                        <span className="text-muted-foreground/70">
                          {item.lastAuthorEmail.split("@")[0]}:{" "}
                        </span>
                      )}
                      {item.lastMessagePreview || "—"}
                    </p>
                  </div>

                  <span className="shrink-0 text-[11px] text-muted-foreground/60">
                    {formatStamp(item.lastMessageUtc)}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      {target && (
        <CommentThreadDrawer
          open={!!target}
          onOpenChange={(o) => !o && setTarget(null)}
          target={target}
        />
      )}
    </div>
  );
}
