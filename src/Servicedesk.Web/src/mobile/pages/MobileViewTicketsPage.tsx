import * as React from "react";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { Loader2, Search, X } from "lucide-react";
import { ticketApi, viewApi, type TicketListQuery, type TicketPage, type ViewSearchMode } from "@/lib/ticket-api";
import { viewDisplayConfig, viewFilters, viewListQuery } from "@/lib/viewFilters";
import { searchableColumns } from "@/lib/ticketColumns";
import { flagColors as resolveFlagColors, useTicketFlagSettings } from "@/lib/ticketFlags";
import { useTicketListRealtime } from "@/hooks/useTicketRealtime";
import { useServerNowMs, useServerTimeZone } from "@/hooks/useServerTime";
import { settingsApi } from "@/lib/api";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { useMobileHeader } from "../mobileHeader";
import { TicketCard } from "../components/TicketCard";

/// Rows per request. The desktop list single-loads up to Tickets.ListPageSize
/// (default 1000); a phone pages instead so the first screen arrives fast on
/// a cellular link. A transport batch size, not a user-facing tunable.
const PAGE_SIZE = 30;

/// What the in-view search matches in Columns mode: the fields a card shows.
/// Passed through `searchableColumns` so only ids on the shared whitelist
/// (server TicketColumnSearch ⇄ client COLUMN_SEARCH_TEXT) are ever sent.
const CARD_SEARCH_FIELDS = searchableColumns([
  "number",
  "subject",
  "requester",
  "companyName",
  "statusName",
  "priorityName",
  "queueName",
  "assigneeEmail",
]);

/// `/m/views/:viewId` — a view's tickets as cards. "all" is the unfiltered
/// "All tickets" list (only offered when the admin shows Open Tickets).
/// Every request carries the view's filters; the server ANDs them with the
/// agent's queue access, so a view never shows a ticket outside either.
export function MobileViewTicketsPage({ viewId }: { viewId: string }) {
  useTicketListRealtime();
  const isAll = viewId === "all";
  const view = useQuery({
    queryKey: ["views", viewId],
    queryFn: () => viewApi.get(viewId),
    enabled: !isAll,
    staleTime: 60_000,
  });
  useMobileHeader(isAll ? "All tickets" : view.data?.name ?? "View", "/m");
  // "All tickets" exists only while the admin shows Open Tickets (desktop
  // /tickets enforces the same). Queue access still applies server-side.
  const nav = useQuery({
    queryKey: ["settings", "navigation"],
    queryFn: settingsApi.navigation,
    staleTime: 60_000,
    enabled: isAll,
  });
  const allHidden = isAll && nav.data !== undefined && !nav.data.showOpenTickets;

  const filters = React.useMemo<TicketListQuery>(() => (view.data ? viewFilters(view.data) : {}), [view.data]);
  const dc = React.useMemo(() => (view.data ? viewDisplayConfig(view.data) : {}), [view.data]);
  const ready = isAll ? nav.data?.showOpenTickets === true : !!view.data;

  // In-view search: same gate and knobs as desktop (the view must enable
  // it; min length + debounce are admin settings). Always server-side here,
  // because a paged list never holds the whole view.
  const searchEnabled = !isAll && dc.searchEnabled === true;
  const searchSettings = useQuery({
    queryKey: ["settings", "view-search"],
    queryFn: ticketApi.viewSearchSettings,
    staleTime: 5 * 60_000,
    enabled: searchEnabled,
  });
  const minChars = searchSettings.data?.minChars ?? 2;
  const debounceMs = searchSettings.data?.debounceMs ?? 300;
  const [term, setTerm] = React.useState("");
  const [mode, setMode] = React.useState<ViewSearchMode | null>(null);
  const searchMode: ViewSearchMode = mode ?? (dc.searchDefaultMode === "full" ? "full" : "columns");
  const debounced = useDebounced(term.trim(), debounceMs);
  const activeTerm = searchEnabled && debounced.length >= minChars ? debounced : "";

  const baseQuery = React.useMemo<TicketListQuery>(() => {
    const q: TicketListQuery = { ...viewListQuery(filters, dc), limit: PAGE_SIZE };
    if (activeTerm) {
      q.q = activeTerm;
      q.qMode = searchMode;
      if (searchMode === "columns") q.qFields = CARD_SEARCH_FIELDS;
    }
    return q;
  }, [filters, dc, activeTerm, searchMode]);

  const list = useInfiniteQuery({
    queryKey: ["tickets", "mobile", viewId, baseQuery],
    enabled: ready,
    initialPageParam: null as PageParam,
    queryFn: ({ pageParam }) => ticketApi.list({ ...baseQuery, ...(pageParam ?? {}) }),
    getNextPageParam: (last: TicketPage): PageParam | undefined => {
      if (last.nextCursor) return { cursorUpdatedUtc: last.nextCursor.updatedUtc, cursorId: last.nextCursor.id };
      if (last.nextOffset != null) return { offset: last.nextOffset };
      return undefined;
    },
    staleTime: 30_000,
  });

  const items = React.useMemo(() => {
    // Live updates can shift rows between pages; never render one twice.
    const seen = new Set<string>();
    return (list.data?.pages ?? []).flatMap((p) => p.items).filter((t) => (seen.has(t.id) ? false : (seen.add(t.id), true)));
  }, [list.data]);

  const sentinel = React.useRef<HTMLDivElement>(null);
  const { hasNextPage, isFetchingNextPage, fetchNextPage } = list;
  React.useEffect(() => {
    const el = sentinel.current;
    if (!el || !hasNextPage) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting) && !isFetchingNextPage) void fetchNextPage();
      },
      { rootMargin: "600px 0px" },
    );
    io.observe(el);
    return () => io.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  const flagSettings = useTicketFlagSettings();
  const colors = React.useMemo(() => resolveFlagColors(flagSettings.data), [flagSettings.data]);
  const serverNowMs = useServerNowMs();
  const zone = useServerTimeZone();

  if (allHidden) {
    return <p className="py-10 text-center text-sm text-muted-foreground">This list is not available. Pick one of your views.</p>;
  }
  if (!isAll && view.isError) {
    return <p className="py-10 text-center text-sm text-muted-foreground">This view does not exist or is not shared with you.</p>;
  }

  return (
    <div className="space-y-3">
      {searchEnabled && (
        <div className="space-y-2">
          <div className="relative">
            <Search className="pointer-events-none absolute left-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <input
              type="search"
              enterKeyHint="search"
              value={term}
              onChange={(e) => setTerm(e.target.value)}
              placeholder="Search this view"
              aria-label="Search this view"
              className="sd-m-input h-12 w-full rounded-xl border border-glass bg-glass pl-10 pr-10 text-base outline-hidden placeholder:text-muted-foreground"
            />
            {term && (
              <button
                type="button"
                onClick={() => setTerm("")}
                className="absolute right-1 top-1/2 inline-flex h-10 w-10 -translate-y-1/2 items-center justify-center text-muted-foreground"
                aria-label="Clear search"
              >
                <X className="h-4 w-4" />
              </button>
            )}
          </div>
          <div className="flex items-center gap-2">
            {(["columns", "full"] as const).map((m) => (
              <button
                key={m}
                type="button"
                onClick={() => setMode(m)}
                className={cn(
                  "h-9 rounded-full border px-3.5 text-[13px] font-medium",
                  searchMode === m ? "border-primary bg-primary text-primary-foreground" : "border-glass bg-glass text-muted-foreground",
                )}
              >
                {m === "columns" ? "Ticket fields" : "Full text"}
              </button>
            ))}
            {term.trim().length > 0 && term.trim().length < minChars && (
              <span className="text-[12px] text-muted-foreground">Type at least {minChars} characters</span>
            )}
          </div>
        </div>
      )}

      {list.isLoading || !ready ? (
        <div className="space-y-2.5">
          {Array.from({ length: 6 }).map((_, i) => (
            <Skeleton key={i} className="h-[104px] w-full rounded-xl" />
          ))}
        </div>
      ) : list.isError ? (
        <p className="py-10 text-center text-sm text-muted-foreground">Could not load tickets. Try again in a moment.</p>
      ) : items.length === 0 ? (
        <p className="py-10 text-center text-sm text-muted-foreground">
          {activeTerm ? "No tickets match your search in this view." : "No tickets in this view."}
        </p>
      ) : (
        <ul className="space-y-2.5">
          {items.map((t) => (
            <li key={t.id}>
              <TicketCard ticket={t} flagColors={colors} serverNowMs={serverNowMs} zone={zone} />
            </li>
          ))}
        </ul>
      )}
      <div ref={sentinel} aria-hidden className="h-px" />
      {isFetchingNextPage && (
        <div className="flex justify-center py-3 text-muted-foreground">
          <Loader2 className="h-5 w-5 animate-spin" />
        </div>
      )}
    </div>
  );
}

type PageParam = { offset: number } | { cursorUpdatedUtc: string; cursorId: string } | null;

function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = React.useState(value);
  React.useEffect(() => {
    const id = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(id);
  }, [value, delayMs]);
  return debounced;
}
