import * as React from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { X } from "lucide-react";
import { ticketApi, contactApi } from "@/lib/ticket-api";
import { agentQueueApi } from "@/lib/api";
import { useTicketRealtime } from "@/hooks/useTicketRealtime";
import { AddNoteForm } from "./components/AddNoteForm";
import { buildMailContext, flattenQueueMailboxes } from "./mailContext";

/// Dedicated compose view for the pop-out-window workflow. Opened by the
/// AddNoteForm's "Pop out" button via `window.open(..., 'sd-compose-<id>', ...)`.
/// Rendered OUTSIDE AppShell (no sidebar, no navbar) so the agent can park
/// the window next to the main servicedesk tab and type a long mail while
/// the main tab stays on the activity feed.
///
/// Sync with the opener window relies on the existing SignalR broadcasts:
/// the backend fires `TicketUpdated` + `TicketListUpdated` on event-add /
/// mail-send, and both windows' React-Query caches invalidate via
/// `useTicketRealtime`. After a successful submit the popup closes
/// itself — the draft is already flushed by the workspace-store's
/// clearDraft call, and the opener's data is refreshed by the SignalR
/// message that arrives <1s later.
export function TicketComposePage({ ticketId }: { ticketId: string }) {
  const queryClient = useQueryClient();
  useTicketRealtime(ticketId);

  const { data, isLoading, isError } = useQuery({
    queryKey: ["ticket", ticketId],
    queryFn: () => ticketApi.get(ticketId),
  });

  const requesterContactId = data?.ticket?.requesterContactId ?? null;
  const { data: requesterContact } = useQuery({
    queryKey: ["contact", requesterContactId],
    queryFn: () => contactApi.get(requesterContactId!),
    enabled: !!requesterContactId,
    staleTime: 300_000,
  });

  const { data: accessibleQueues } = useQuery({
    queryKey: ["accessible-queues"],
    queryFn: agentQueueApi.list,
    staleTime: 60_000,
  });
  const ownMailboxAddresses = React.useMemo(
    () => flattenQueueMailboxes(accessibleQueues),
    [accessibleQueues],
  );

  // Give the window a meaningful title so the taskbar / alt-tab shows
  // which ticket the popup belongs to.
  React.useEffect(() => {
    if (!data?.ticket) return;
    const prev = document.title;
    document.title = `#${data.ticket.number} — Compose · ${data.ticket.subject}`;
    return () => {
      document.title = prev;
    };
  }, [data?.ticket]);

  if (isLoading) {
    return (
      <div className="min-h-screen bg-background p-6 text-sm text-muted-foreground">
        Loading…
      </div>
    );
  }
  if (isError || !data) {
    return (
      <div className="min-h-screen bg-background p-6 text-sm text-destructive">
        Kon ticket niet laden.
      </div>
    );
  }

  const { ticket, events } = data;
  const mailContext = buildMailContext(
    ticket,
    events,
    requesterContact?.email ?? null,
    ownMailboxAddresses,
  );

  return (
    <div className="min-h-screen bg-background">
      <header className="sticky top-0 z-10 border-b border-white/5 bg-background/95 px-5 py-3 backdrop-blur">
        <div className="flex items-start gap-3">
          <div className="min-w-0 flex-1">
            <div className="text-[11px] uppercase tracking-[0.22em] text-muted-foreground">
              Compose · Ticket
            </div>
            <h1 className="truncate font-display text-base font-semibold">
              <span className="font-mono text-sm text-muted-foreground">#{ticket.number}</span>
              <span className="mx-2 text-muted-foreground/40">·</span>
              <span>{ticket.subject}</span>
            </h1>
          </div>
          <button
            type="button"
            title="Close window"
            onClick={() => window.close()}
            className="shrink-0 rounded-md p-1.5 text-muted-foreground transition-colors hover:bg-white/[0.06] hover:text-foreground"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
      </header>

      <main className="mx-auto max-w-3xl px-5 py-6">
        <AddNoteForm
          key={ticketId}
          ticketId={ticketId}
          mailContext={mailContext}
          isPopup
          onSubmitted={() => {
            queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
            // The opener's ticket query refreshes itself via the SignalR
            // broadcast the server emitted on event-add / mail-send, so
            // we just close; no cross-window messaging needed.
            window.close();
          }}
        />
      </main>
    </div>
  );
}
