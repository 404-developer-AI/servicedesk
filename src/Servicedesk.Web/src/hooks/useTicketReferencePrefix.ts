import { useQuery } from "@tanstack/react-query";
import { systemApi } from "@/lib/api";
import { DEFAULT_TICKET_REFERENCE_PREFIX } from "@/lib/ticketRef";

/// Reads the admin-configurable ticket reference prefix (e.g. "Ticket#") once
/// and caches it for the session — it changes only when an admin edits the
/// setting. Falls back to the factory default while loading or on error so the
/// copy button always produces a sane reference.
export function useTicketReferencePrefix(): string {
  const { data } = useQuery({
    queryKey: ["system", "ticket-reference-prefix"],
    queryFn: systemApi.ticketReferencePrefix,
    staleTime: Infinity,
    gcTime: Infinity,
    retry: 1,
  });
  const prefix = data?.prefix?.trim();
  return prefix && prefix.length > 0 ? prefix : DEFAULT_TICKET_REFERENCE_PREFIX;
}
