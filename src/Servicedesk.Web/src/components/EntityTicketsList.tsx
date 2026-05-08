import * as React from "react";
import { useInfiniteQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Search, Ticket as TicketIcon } from "lucide-react";
import { ticketApi, type TicketListItem, type TicketListQuery } from "@/lib/ticket-api";
import { useTicketListRealtime } from "@/hooks/useTicketRealtime";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

const PAGE_SIZE = 25;
const DEBOUNCE_MS = 200;

type Props =
  | { requesterContactId: string; companyId?: undefined; searchScope?: undefined; initialSearch?: string }
  | { companyId: string; requesterContactId?: undefined; searchScope?: undefined; initialSearch?: string }
  | { searchScope: "global"; requesterContactId?: undefined; companyId?: undefined; initialSearch?: string };

/// Tickets list scoped to a contact, company, or a free-text search.
/// Reuses /api/tickets (queue-access enforced server-side) with `openFirst`
/// so resolved/closed rows sort below open ones. Auto-scroll mirrors the
/// main ticket list pattern; SignalR `TicketListUpdated` invalidates
/// ["tickets"] so this view refreshes whenever the broader list does. When
/// embedded in the search-context-bar a row click navigates with
/// from/entityId/q params so the pill follows the agent across tickets.
export function EntityTicketsList(props: Props) {
  const navigate = useNavigate();
  const scope: "contact" | "company" | "search" =
    props.searchScope === "global" ? "search" : props.companyId ? "company" : "contact";

  const [search, setSearch] = React.useState(props.initialSearch ?? "");
  const [debounced, setDebounced] = React.useState((props.initialSearch ?? "").trim());

  React.useEffect(() => {
    const t = setTimeout(() => setDebounced(search.trim()), DEBOUNCE_MS);
    return () => clearTimeout(t);
  }, [search]);

  useTicketListRealtime();

  const filters = React.useMemo<TicketListQuery>(() => ({
    requesterContactId: props.requesterContactId,
    companyId: props.companyId,
    search: debounced || undefined,
    openFirst: true,
    limit: PAGE_SIZE,
  }), [props.requesterContactId, props.companyId, debounced]);

  // Global search-only mode requires a non-empty query — without one we'd be
  // listing every ticket in the system.
  const enabled = scope !== "search" || debounced.length > 0;

  const {
    data,
    isLoading,
    isError,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
  } = useInfiniteQuery({
    queryKey: ["tickets", "entity", scope, props.requesterContactId ?? props.companyId ?? "global", debounced],
    queryFn: ({ pageParam }: { pageParam: number }) =>
      ticketApi.list({ ...filters, offset: pageParam }),
    initialPageParam: 0,
    getNextPageParam: (lastPage) => lastPage.nextOffset ?? undefined,
    staleTime: 30_000,
    enabled,
  });

  const sentinelRef = React.useRef<HTMLDivElement | null>(null);
  React.useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: "200px" },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  const items: TicketListItem[] = data?.pages.flatMap((p) => p.items) ?? [];
  const firstClosedIdx = items.findIndex(
    (t) => t.statusStateCategory === "Resolved" || t.statusStateCategory === "Closed",
  );
  const openItems = firstClosedIdx === -1 ? items : items.slice(0, firstClosedIdx);
  const closedItems = firstClosedIdx === -1 ? [] : items.slice(firstClosedIdx);

  const emptyMessage = debounced
    ? `No tickets match "${debounced}".`
    : scope === "contact"
      ? "No tickets linked to this contact yet."
      : scope === "company"
        ? "No tickets linked to this company yet."
        : "Type to search across all tickets.";

  // Forward search-context params on row click so the pill stays alive when
  // the agent jumps from one ticket to the next.
  function openTicket(id: string) {
    const fromParams: Record<string, string> = {};
    if (scope === "contact" && props.requesterContactId) {
      fromParams.from = "contact";
      fromParams.entityId = props.requesterContactId;
    } else if (scope === "company" && props.companyId) {
      fromParams.from = "company";
      fromParams.entityId = props.companyId;
    } else if (scope === "search") {
      fromParams.from = "search";
    }
    if (debounced) fromParams.q = debounced;
    navigate({
      to: "/tickets/$ticketId" as never,
      params: { ticketId: id } as never,
      search: fromParams as never,
    });
  }

  return (
    <section className="glass-card p-5">
      <div className="flex flex-col gap-1">
        <div className="flex items-center justify-between gap-2">
          <h2 className="flex items-center gap-2 text-sm font-medium uppercase tracking-wide text-muted-foreground">
            <TicketIcon className="h-3.5 w-3.5" /> Tickets
          </h2>
          <span className="text-[11px] text-muted-foreground/70">
            {items.length} loaded
            {hasNextPage && " · more available"}
          </span>
        </div>
        <div className="relative mt-3">
          <Search className="pointer-events-none absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder={
              scope === "contact"
                ? "Search this contact's tickets…"
                : scope === "company"
                  ? "Search this company's tickets…"
                  : "Search tickets…"
            }
            className="pl-9"
          />
        </div>
      </div>

      <div className="mt-4">
        {!enabled ? (
          <p className="rounded-md border border-white/5 bg-white/[0.02] p-6 text-center text-sm text-muted-foreground">
            {emptyMessage}
          </p>
        ) : isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-12 w-full" />
            <Skeleton className="h-12 w-full" />
            <Skeleton className="h-12 w-full" />
          </div>
        ) : isError ? (
          <p className="rounded-md border border-red-500/20 bg-red-500/[0.04] p-3 text-sm text-red-300">
            Could not load tickets. Try refreshing.
          </p>
        ) : items.length === 0 ? (
          <p className="rounded-md border border-white/5 bg-white/[0.02] p-6 text-center text-sm text-muted-foreground">
            {emptyMessage}
          </p>
        ) : (
          <div className="space-y-1.5">
            {openItems.map((t) => (
              <TicketRow
                key={t.id}
                ticket={t}
                scope={scope}
                onOpen={() => openTicket(t.id)}
              />
            ))}

            {closedItems.length > 0 && (
              <div className="flex items-center gap-3 px-1 pt-3 pb-1">
                <div className="h-px flex-1 bg-white/5" />
                <span className="text-[10px] font-medium uppercase tracking-[0.18em] text-muted-foreground/60">
                  Resolved &amp; closed
                </span>
                <div className="h-px flex-1 bg-white/5" />
              </div>
            )}
            {closedItems.map((t) => (
              <TicketRow
                key={t.id}
                ticket={t}
                scope={scope}
                onOpen={() => openTicket(t.id)}
              />
            ))}

            <div ref={sentinelRef} className="h-1" />
            {isFetchingNextPage && (
              <div className="space-y-2 pt-2">
                <Skeleton className="h-12 w-full" />
                <Skeleton className="h-12 w-full" />
              </div>
            )}
          </div>
        )}
      </div>
    </section>
  );
}

function TicketRow({
  ticket: t,
  scope,
  onOpen,
}: {
  ticket: TicketListItem;
  scope: "contact" | "company" | "search";
  onOpen: () => void;
}) {
  const { time } = useServerTime();
  const offset = time?.offsetMinutes ?? 0;
  const requesterName =
    [t.requesterFirstName, t.requesterLastName].filter(Boolean).join(" ").trim() ||
    t.requesterEmail;
  const statusColor = t.statusColor || "#6b7280";
  const priorityColor = t.priorityColor || "#6b7280";

  return (
    <button
      type="button"
      onClick={onOpen}
      className={cn(
        "group relative flex w-full items-center gap-3 overflow-hidden rounded-md border border-white/5 bg-white/[0.02] py-2 pl-4 pr-3 text-left transition-colors",
        "hover:border-white/10 hover:bg-white/[0.05]",
      )}
    >
      <span
        aria-hidden
        className="absolute inset-y-1 left-1 w-1 rounded-full"
        style={{ backgroundColor: statusColor }}
      />
      <span className="w-14 shrink-0 font-mono text-xs text-primary">#{t.number}</span>
      <span
        className="min-w-0 flex-1 truncate text-sm text-foreground/90"
        title={t.subject}
      >
        {t.subject}
      </span>
      {(scope === "company" || scope === "search") && (
        <span
          className="hidden min-w-0 max-w-[140px] shrink truncate text-xs text-muted-foreground md:inline"
          title={requesterName}
        >
          {requesterName}
        </span>
      )}
      <ColoredPill label={t.statusName} color={statusColor} />
      <ColoredPill label={t.priorityName} color={priorityColor} />
      <span className="hidden shrink-0 text-xs text-muted-foreground/80 sm:inline">
        {t.assigneeEmail ? (
          <span title={t.assigneeEmail}>{shortEmail(t.assigneeEmail)}</span>
        ) : (
          <span className="italic text-muted-foreground/60">Unassigned</span>
        )}
      </span>
      <span className="hidden w-20 shrink-0 text-right text-[11px] text-muted-foreground/70 sm:inline">
        {time ? toServerLocal(t.updatedUtc, offset) : "…"}
      </span>
    </button>
  );
}

function ColoredPill({ label, color }: { label: string; color: string }) {
  return (
    <span
      className="shrink-0 rounded px-2 py-0.5 text-[11px] font-medium"
      style={{ backgroundColor: `${color}20`, color }}
    >
      {label}
    </span>
  );
}

function shortEmail(email: string): string {
  const at = email.indexOf("@");
  return at > 0 ? email.slice(0, at) : email;
}
