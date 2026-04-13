import * as React from "react";
import { useQuery, useInfiniteQuery } from "@tanstack/react-query";
import { useNavigate, useRouterState } from "@tanstack/react-router";
import { Eye, Loader2, ShieldAlert, Ticket } from "lucide-react";
import { ColumnSelector } from "@/components/ColumnSelector";
import { TicketTableSkeleton } from "./components/TicketTable";
import { GroupedTicketList } from "./components/GroupedTicketList";
import { ticketApi, viewApi } from "@/lib/ticket-api";
import { agentQueueApi, settingsApi } from "@/lib/api";
import { useColumnPrefsStore } from "@/stores/useColumnPrefsStore";
import { useTicketListRealtime } from "@/hooks/useTicketRealtime";
import type { TicketListQuery, TicketListItem, DisplayConfig } from "@/lib/ticket-api";

function readFiltersFromSearch(searchStr: string): TicketListQuery {
  const params = new URLSearchParams(searchStr);
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
  useTicketListRealtime();
  const navigate = useNavigate();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const searchStr = useRouterState({ select: (s) => s.location.searchStr });
  const viewId = React.useMemo(
    () => new URLSearchParams(searchStr).get("viewId"),
    [searchStr],
  );
  const setActiveView = useColumnPrefsStore((s) => s.setActiveView);

  React.useEffect(() => {
    setActiveView(viewId);
  }, [viewId, setActiveView]);

  // Check accessible queues — if the agent has none, show a "no access" message
  const { data: accessibleQueues } = useQuery({
    queryKey: ["accessible-queues"],
    queryFn: agentQueueApi.list,
    staleTime: 60_000,
  });

  // Redirect away when Open Tickets is toggled off and no saved view is active
  const { data: navSettings } = useQuery({
    queryKey: ["settings", "navigation"],
    queryFn: settingsApi.navigation,
    staleTime: 60_000,
  });

  React.useEffect(() => {
    // Guard: only redirect while this component actually owns the /tickets path.
    // During a route transition (e.g. clicking a ticket row) the router state
    // updates before unmount, making viewId briefly null — without this check
    // the redirect would hijack the pending navigation.
    if (pathname !== "/tickets") return;
    if (navSettings && !navSettings.showOpenTickets && !viewId) {
      navigate({ to: "/" });
    }
  }, [navSettings, viewId, navigate, pathname]);

  const [viewApplied, setViewApplied] = React.useState(!viewId);
  const [appliedViewId, setAppliedViewId] = React.useState(viewId);
  const [filters, setFilters] = React.useState<TicketListQuery>(() =>
    readFiltersFromSearch(searchStr),
  );
  const [displayConfig, setDisplayConfig] = React.useState<DisplayConfig>({});

  // Reset state when the viewId in the URL changes (client-side navigation).
  React.useEffect(() => {
    if (viewId !== appliedViewId) {
      setAppliedViewId(viewId);
      setViewApplied(!viewId);
      if (!viewId) {
        setFilters(readFiltersFromSearch(searchStr));
        setDisplayConfig({});
      }
    }
  }, [viewId]); // eslint-disable-line react-hooks/exhaustive-deps

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
    // Parse display config (sorting, grouping, priority float)
    try {
      const dc: DisplayConfig = viewData.displayConfigJson
        ? JSON.parse(viewData.displayConfigJson)
        : {};
      setDisplayConfig(dc);
    } catch {
      setDisplayConfig({});
    }
    setViewApplied(true);
  }, [viewData, viewApplied]);

  type PageParam =
    | { type: "cursor"; updatedUtc: string; id: string }
    | { type: "offset"; offset: number }
    | null;

  const {
    data,
    isLoading: ticketsLoading,
    isError,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
  } = useInfiniteQuery({
    queryKey: ["tickets", filters, displayConfig],
    queryFn: ({ pageParam }: { pageParam: PageParam }) => {
      const query: TicketListQuery = {
        ...filters,
        sortField: displayConfig.sort?.field,
        sortDirection: displayConfig.sort?.direction,
        priorityFloat: displayConfig.priorityFloat,
      };
      if (pageParam?.type === "cursor") {
        query.cursorUpdatedUtc = pageParam.updatedUtc;
        query.cursorId = pageParam.id;
      } else if (pageParam?.type === "offset") {
        query.offset = pageParam.offset;
      }
      return ticketApi.list(query);
    },
    initialPageParam: null as PageParam,
    getNextPageParam: (lastPage): PageParam | undefined => {
      if (lastPage.nextCursor)
        return { type: "cursor", ...lastPage.nextCursor };
      if (lastPage.nextOffset != null)
        return { type: "offset", offset: lastPage.nextOffset };
      return undefined;
    },
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

  const hasNoQueueAccess = accessibleQueues !== undefined && accessibleQueues.length === 0;

  if (hasNoQueueAccess) {
    return (
      <div className="flex flex-col items-center justify-center h-[calc(100vh-6rem)] gap-4">
        <div className="glass-card p-12 flex flex-col items-center gap-4 text-center max-w-md">
          <ShieldAlert className="h-12 w-12 text-muted-foreground/40" />
          <div>
            <p className="text-lg font-semibold text-foreground">No queue access</p>
            <p className="text-sm text-muted-foreground mt-2">
              You have not been assigned to any queues yet. Contact your administrator to get access.
            </p>
          </div>
        </div>
      </div>
    );
  }

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
            <GroupedTicketList
              items={allItems}
              displayConfig={displayConfig}
              onRowClick={handleRowClick}
            />
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
