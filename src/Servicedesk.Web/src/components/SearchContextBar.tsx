import * as React from "react";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ChevronDown, ChevronUp, X } from "lucide-react";
import { contactApi, companyApi } from "@/lib/ticket-api";
import { EntityTicketsList } from "@/components/EntityTicketsList";
import { cn } from "@/lib/utils";

type FromKind = "search" | "contact" | "company";

type SearchContextParams = {
  from?: string;
  entityId?: string;
  q?: string;
};

/// Persistent "back to where you were searching" pill that appears at the top
/// of the ticket-detail page whenever the agent arrived via the global
/// search bar, the full search page, or a contact / company tickets-tab.
/// Click the pill to expand the original list inline; click another result
/// to jump to that ticket while keeping the pill alive. The X clears the
/// from-context entirely, leaving a clean ticket URL.
export function SearchContextBar({ ticketId }: { ticketId: string }) {
  const navigate = useNavigate();
  const params = useSearch({ strict: false }) as SearchContextParams;
  const [expanded, setExpanded] = React.useState(false);

  const fromRaw = params.from;
  const isValidFrom: FromKind | null =
    fromRaw === "search" || fromRaw === "contact" || fromRaw === "company"
      ? (fromRaw as FromKind)
      : null;

  const entityId = params.entityId;
  const q = params.q ?? "";

  const contactQ = useQuery({
    queryKey: ["contact", entityId],
    queryFn: () => contactApi.get(entityId!),
    enabled: isValidFrom === "contact" && !!entityId,
    staleTime: 60_000,
  });
  const companyQ = useQuery({
    queryKey: ["companies", "detail", entityId],
    queryFn: () => companyApi.get(entityId!),
    enabled: isValidFrom === "company" && !!entityId,
    staleTime: 60_000,
  });

  // Reset expansion when the agent navigates between tickets so each new
  // ticket starts with the panel collapsed (the pill remains visible).
  React.useEffect(() => {
    setExpanded(false);
  }, [ticketId]);

  if (!isValidFrom) return null;
  if ((isValidFrom === "contact" || isValidFrom === "company") && !entityId) return null;

  let pillLabel = "";
  if (isValidFrom === "search") {
    pillLabel = q ? `Search: "${q}"` : "Search";
  } else if (isValidFrom === "contact") {
    const c = contactQ.data;
    const name = c
      ? [c.firstName, c.lastName].filter(Boolean).join(" ").trim() || c.email
      : "Contact";
    pillLabel = q ? `${name} · "${q}"` : `${name} · Tickets`;
  } else {
    const co = companyQ.data?.company;
    const name = co?.name ?? "Company";
    pillLabel = q ? `${name} · "${q}"` : `${name} · Tickets`;
  }

  function dismiss() {
    navigate({
      to: "/tickets/$ticketId" as never,
      params: { ticketId } as never,
      search: {} as never,
    });
  }

  return (
    <div className="sticky top-0 z-30 mb-3">
      <div
        className={cn(
          "glass-card flex items-center gap-2 px-3 py-1.5",
          "border border-glass bg-glass backdrop-blur",
        )}
      >
        <button
          type="button"
          onClick={() => setExpanded((v) => !v)}
          className={cn(
            "group flex flex-1 items-center gap-2 rounded-md px-2 py-1 text-left text-xs transition-colors",
            "hover:bg-glass-hover",
          )}
          title={expanded ? "Collapse results" : "Expand results"}
        >
          <ArrowLeft className="h-3.5 w-3.5 shrink-0 text-muted-foreground transition-colors group-hover:text-foreground" />
          <span className="truncate text-foreground/90">{pillLabel}</span>
          {expanded ? (
            <ChevronUp className="ml-auto h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          ) : (
            <ChevronDown className="ml-auto h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          )}
        </button>
        <button
          type="button"
          onClick={dismiss}
          title="Dismiss"
          aria-label="Dismiss search context"
          className="shrink-0 rounded-md p-1 text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>

      {expanded && (
        <div className="mt-2 max-h-[60vh] overflow-y-auto">
          {isValidFrom === "contact" && entityId && (
            <EntityTicketsList requesterContactId={entityId} initialSearch={q} />
          )}
          {isValidFrom === "company" && entityId && (
            <EntityTicketsList companyId={entityId} initialSearch={q} />
          )}
          {isValidFrom === "search" && (
            <EntityTicketsList searchScope="global" initialSearch={q} />
          )}
        </div>
      )}
    </div>
  );
}
