import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Ticket } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ColumnSelector } from "@/components/ColumnSelector";
import { TicketFilters } from "./components/TicketFilters";
import { TicketTable, TicketTableSkeleton } from "./components/TicketTable";
import { ticketApi, viewApi } from "@/lib/ticket-api";
import type { TicketListQuery } from "@/lib/ticket-api";

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
  const cursorUpdatedUtc = params.get("cursorUpdatedUtc");
  const cursorId = params.get("cursorId");
  if (queueId) filters.queueId = queueId;
  if (statusId) filters.statusId = statusId;
  if (priorityId) filters.priorityId = priorityId;
  if (assigneeUserId) filters.assigneeUserId = assigneeUserId;
  if (search) filters.search = search;
  if (openOnly === "true") filters.openOnly = true;
  if (cursorUpdatedUtc) filters.cursorUpdatedUtc = cursorUpdatedUtc;
  if (cursorId) filters.cursorId = cursorId;
  return filters;
}

function writeFiltersToSearch(filters: TicketListQuery) {
  const params = new URLSearchParams();
  if (filters.queueId) params.set("queueId", filters.queueId);
  if (filters.statusId) params.set("statusId", filters.statusId);
  if (filters.priorityId) params.set("priorityId", filters.priorityId);
  if (filters.assigneeUserId) params.set("assigneeUserId", filters.assigneeUserId);
  if (filters.search) params.set("search", filters.search);
  if (filters.openOnly) params.set("openOnly", "true");
  if (filters.cursorUpdatedUtc) params.set("cursorUpdatedUtc", filters.cursorUpdatedUtc);
  if (filters.cursorId) params.set("cursorId", filters.cursorId);
  const qs = params.toString();
  const url = `${window.location.pathname}${qs ? `?${qs}` : ""}`;
  window.history.replaceState(null, "", url);
}

type CursorEntry = { updatedUtc: string; id: string };

export function TicketListPage() {
  const navigate = useNavigate();
  const [viewId] = React.useState(getViewIdFromSearch);
  const [viewApplied, setViewApplied] = React.useState(!viewId);
  const [filters, setFilters] = React.useState<TicketListQuery>(() =>
    readFiltersFromSearch(),
  );
  const [cursorStack, setCursorStack] = React.useState<CursorEntry[]>([]);

  // When navigating via a saved view (?viewId=...), fetch the view and apply
  // its stored filters so the ticket list shows the correct subset.
  const { data: viewData } = useQuery({
    queryKey: ["views", viewId],
    queryFn: () => viewApi.get(viewId!),
    enabled: !!viewId && !viewApplied,
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
      writeFiltersToSearch(applied);
    } catch {
      // bad JSON — ignore, show unfiltered
    }
    setViewApplied(true);
  }, [viewData, viewApplied]);

  const { data, isLoading: ticketsLoading, isError } = useQuery({
    queryKey: ["tickets", filters],
    queryFn: () => ticketApi.list(filters),
    staleTime: 30_000,
  });

  function handleFiltersChange(next: TicketListQuery) {
    setCursorStack([]);
    setFilters(next);
    writeFiltersToSearch(next);
  }

  function handleNextPage() {
    if (!data?.nextCursor) return;
    const cursor = data.nextCursor;
    setCursorStack((prev) => [...prev, cursor]);
    const next: TicketListQuery = {
      ...filters,
      cursorUpdatedUtc: cursor.updatedUtc,
      cursorId: cursor.id,
    };
    setFilters(next);
    writeFiltersToSearch(next);
  }

  function handleFirstPage() {
    setCursorStack([]);
    const next: TicketListQuery = {
      ...filters,
      cursorUpdatedUtc: undefined,
      cursorId: undefined,
    };
    setFilters(next);
    writeFiltersToSearch(next);
  }

  function handleRowClick(id: string) {
    navigate({ to: "/tickets/$id" as never, params: { id } as never });
  }

  const isLoading = ticketsLoading || (!!viewId && !viewApplied);
  const isOnFirstPage = !filters.cursorUpdatedUtc && !filters.cursorId;
  const ticketCount = data?.items.length ?? 0;

  return (
    <div className="flex flex-col gap-4 h-[calc(100vh-8rem)]">
      <header className="flex items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/20 border border-primary/30">
            <Ticket className="h-4 w-4 text-primary" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold text-foreground leading-tight">
              Tickets
            </h1>
            {!isLoading && (
              <p className="text-xs text-muted-foreground">
                {ticketCount} ticket{ticketCount !== 1 ? "s" : ""}
                {data?.nextCursor ? "+" : ""} on this page
              </p>
            )}
          </div>
        </div>
        <ColumnSelector />
      </header>

      <TicketFilters filters={filters} onChange={handleFiltersChange} />

      <div className="flex-1 min-h-0 overflow-y-auto">
        {isLoading ? (
          <TicketTableSkeleton />
        ) : isError ? (
          <div className="glass-card p-8 text-center text-sm text-destructive">
            Failed to load tickets. Please try again.
          </div>
        ) : data && data.items.length > 0 ? (
          <TicketTable data={data.items} onRowClick={handleRowClick} />
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

      <footer className="flex items-center justify-between text-xs text-muted-foreground shrink-0">
        <span>
          {cursorStack.length > 0
            ? `Page ${cursorStack.length + 1}`
            : "Page 1"}
        </span>
        <div className="flex gap-2">
          <Button
            variant="ghost"
            size="sm"
            disabled={isOnFirstPage}
            onClick={handleFirstPage}
          >
            First page
          </Button>
          <Button
            variant="secondary"
            size="sm"
            disabled={!data?.nextCursor}
            onClick={handleNextPage}
          >
            Next →
          </Button>
        </div>
      </footer>
    </div>
  );
}
