import * as React from "react";
import { useQuery, useInfiniteQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Eye, Loader2, Ticket } from "lucide-react";
import { ColumnSelector } from "@/components/ColumnSelector";
import { TicketTable, TicketTableSkeleton } from "./components/TicketTable";
import { ticketApi, viewApi } from "@/lib/ticket-api";
import type { TicketListQuery, TicketListItem } from "@/lib/ticket-api";

function getViewIdFromSearch(): string | null {
  if (typeof window === "undefined") return null;
  return new URLSearchParams(window.location.search).get("viewId");
}

function readFiltersFromSearch(): TicketListQuery {
  if (typeof window === "undefined") return {};
  const params = new URLSearchParams(window.location.search);
  const filters: TicketListQuery = {};
  const queueId = params.get("queueId");
  const statusId = params.get("statusId");
  const priorityId = params.get("priorityId");
  const assigneeUserId = params.get("assigneeUserId");
  const search = params.get("search");
  const openOnly = params.get("openOnly");
  if (queueId) filters.queueId = queueId;
  if (statusId) filters.statusId = statusId;
  if (priorityId) filters.priorityId = priorityId;
  if (assigneeUserId) filters.assigneeUserId = assigneeUserId;
  if (search) filters.search = search;
  if (openOnly === "true") filters.openOnly = true;
  return filters;
}

export function TicketListPage() {
  const navigate = useNavigate();
  const [viewId] = React.useState(getViewIdFromSearch);
  const [viewApplied, setViewApplied] = React.useState(!viewId);
  const [filters, setFilters] = React.useState<TicketListQuery>(() =>
    readFiltersFromSearch(),
  );

  // When navigating via a saved view (?viewId=...), fetch the view and apply
  // its stored filters so the ticket list shows the correct subset.
  const { data: viewData } = useQuery({
    queryKey: ["views", viewId],
    queryFn: () => viewApi.get(viewId!),
    enabled: !!viewId,
    staleTime: Infinity,
  });

  React.useEffect(() => {
    if (!viewData || viewApplied) return;
    try {
      const vf = JSON.parse(viewData.filtersJson) as Record<string, unknown>;
      const applied: TicketListQuery = {};
      if (typeof vf.queueId === "string") applied.queueId = vf.queueId;
      if (typeof vf.statusId === "string") applied.statusId = vf.statusId;
      if (typeof vf.priorityId === "string") applied.priorityId = vf.priorityId;
      if (typeof vf.assigneeUserId === "string") applied.assigneeUserId = vf.assigneeUserId;
      if (typeof vf.search === "string") applied.search = vf.search;
      if (vf.openOnly === true) applied.openOnly = true;
      setFilters(applied);
    } catch {
      // bad JSON — ignore, show unfiltered
    }
    setViewApplied(true);
  }, [viewData, viewApplied]);

  const {
    data,
    isLoading: ticketsLoading,
    isError,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
  } = useInfiniteQuery({
    queryKey: ["tickets", filters],
    queryFn: ({ pageParam }) => {
      const query: TicketListQuery = { ...filters };
      if (pageParam) {
        query.cursorUpdatedUtc = pageParam.updatedUtc;
        query.cursorId = pageParam.id;
      }
      return ticketApi.list(query);
    },
    initialPageParam: null as { updatedUtc: string; id: string } | null,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    staleTime: 30_000,
    enabled: viewApplied,
  });

  // Infinite scroll: observe a sentinel element at the bottom of the list
  const sentinelRef = React.useRef<HTMLDivElement>(null);
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

  function handleRowClick(id: string) {
    navigate({ to: "/tickets/$id" as never, params: { id } as never });
  }

  const isLoading = ticketsLoading || (!!viewId && !viewApplied);
  const allItems: TicketListItem[] = data?.pages.flatMap((p) => p.items) ?? [];

  const pageTitle = viewData?.name ?? "Tickets";
  const PageIcon = viewId ? Eye : Ticket;

  return (
    <div className="flex flex-col gap-4 h-[calc(100vh-3rem)]">
      <header className="flex items-center justify-between gap-4 shrink-0">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/20 border border-primary/30">
            <PageIcon className="h-4 w-4 text-primary" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold text-foreground leading-tight">
              {pageTitle}
            </h1>
            {!isLoading && (
              <p className="text-xs text-muted-foreground">
                {allItems.length} ticket{allItems.length !== 1 ? "s" : ""}
                {hasNextPage ? "+" : ""}
              </p>
            )}
          </div>
        </div>
        <ColumnSelector />
      </header>

      <div className="flex-1 min-h-0 overflow-y-auto">
        {isLoading ? (
          <TicketTableSkeleton />
        ) : isError ? (
          <div className="glass-card p-8 text-center text-sm text-destructive">
            Failed to load tickets. Please try again.
          </div>
        ) : allItems.length > 0 ? (
          <>
            <TicketTable data={allItems} onRowClick={handleRowClick} />
            <div ref={sentinelRef} className="h-1" />
            {isFetchingNextPage && (
              <div className="flex items-center justify-center py-4">
                <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
              </div>
            )}
          </>
        ) : (
          <div className="glass-card p-12 flex flex-col items-center justify-center gap-3 text-center">
            <Ticket className="h-10 w-10 text-muted-foreground/40" />
            <div>
              <p className="text-sm font-medium text-foreground">No tickets found</p>
              <p className="text-xs text-muted-foreground mt-1">
                Try adjusting your filters or search query.
              </p>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
