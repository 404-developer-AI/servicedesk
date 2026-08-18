import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ticketApi } from "@/lib/ticket-api";
import { useTicketRealtime } from "@/hooks/useTicketRealtime";
import { TicketChecklistPanel } from "./components/checklists/TicketChecklistPanel";
import { useChecklistSettings, useTicketChecklists } from "./components/checklists/useTicketChecklists";

/// v0.0.103 — stand-alone checklist window (opened from the docked panel's
/// "Open in a separate window" action). Rendered outside AppShell like the
/// compose pop-out; stays in sync with the main tab through the same
/// SignalR `TicketUpdated` pushes (every checklist mutation ends with one).
export function TicketChecklistPopoutPage({ ticketId }: { ticketId: string }) {
  useTicketRealtime(ticketId);
  const settingsQ = useChecklistSettings();
  const enabled = settingsQ.data?.enabled ?? false;
  const { checklists, isLoading } = useTicketChecklists(ticketId, enabled);

  const { data: ticketData } = useQuery({
    queryKey: ["ticket", ticketId],
    queryFn: () => ticketApi.get(ticketId),
  });

  const initial = React.useMemo(() => new URLSearchParams(window.location.search).get("checklist"), []);
  const [activeId, setActiveId] = React.useState<string | null>(initial);

  React.useEffect(() => {
    if (!ticketData?.ticket) return;
    const prev = document.title;
    document.title = `#${ticketData.ticket.number} — Checklist · ${ticketData.ticket.subject}`;
    return () => {
      document.title = prev;
    };
  }, [ticketData?.ticket]);

  return (
    <div className="h-screen w-screen bg-background p-3">
      {ticketData?.ticket && (
        <div className="mb-2 px-1 text-xs text-muted-foreground truncate">
          <span className="font-mono text-primary">#{ticketData.ticket.number}</span>{" "}
          <span className="text-foreground/80">{ticketData.ticket.subject}</span>
        </div>
      )}
      {settingsQ.isLoading || isLoading ? (
        <div className="glass-panel h-[calc(100%-1.75rem)] p-4 text-sm text-muted-foreground">Loading checklist…</div>
      ) : !enabled ? (
        <div className="glass-panel h-[calc(100%-1.75rem)] p-4 text-sm text-muted-foreground">
          Checklists are turned off in Settings → Tickets → Checklists.
        </div>
      ) : (
        <TicketChecklistPanel
          ticketId={ticketId}
          checklists={checklists}
          settings={settingsQ.data!}
          activeChecklistId={activeId}
          onActiveChange={setActiveId}
          mode="popout"
          className="h-[calc(100%-1.75rem)]"
        />
      )}
    </div>
  );
}
