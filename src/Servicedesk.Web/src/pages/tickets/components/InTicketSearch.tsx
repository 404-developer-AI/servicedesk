import { useCallback, useMemo } from "react";
import type { TicketEvent } from "@/lib/ticket-api";
import { InPageSearchProvider, useInPageSearch } from "@/components/InPageSearch";

/// In-ticket search — thin adapter over the generic InPageSearch (which
/// owns the Ctrl+F bar, Filter/Highlight modes and DOM highlighting).
/// Adds the ticket-specific matcher: an event matches when the query is
/// found in its body, event type or author name.

export function InTicketSearchProvider({ children }: { children: React.ReactNode }) {
  return (
    <InPageSearchProvider placeholder="Search in this ticket…">
      {children}
    </InPageSearchProvider>
  );
}

export function useInTicketSearch() {
  const ctx = useInPageSearch();
  const { matchesText } = ctx;

  const matchesEvent = useCallback(
    (evt: TicketEvent) =>
      matchesText(
        `${evt.bodyText ?? ""} ${stripHtml(evt.bodyHtml)} ${evt.eventType} ${evt.authorName ?? ""}`,
      ),
    [matchesText],
  );

  return useMemo(() => ({ ...ctx, matchesEvent }), [ctx, matchesEvent]);
}

function stripHtml(html: string | null | undefined): string {
  if (!html) return "";
  return html.replace(/<[^>]+>/g, " ");
}
