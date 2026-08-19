import { useEffect, useState } from "react";
import { Link, useNavigate } from "@tanstack/react-router";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, Inbox, Plus, Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { portalTicketApi } from "@/lib/portal-api";
import { cn } from "@/lib/utils";
import { PriorityDot, StatusPill, usePortalDates, usePortalMe } from "@/portal/portalShared";
import { usePortalConfig } from "@/portal/PortalAuthLayout";

type Filter = "open" | "closed" | "all";

const FILTERS: { key: Filter; label: string }[] = [
  { key: "open", label: "Open" },
  { key: "closed", label: "Closed" },
  { key: "all", label: "All" },
];

export function PortalTicketsPage() {
  const navigate = useNavigate();
  const me = usePortalMe();
  const config = usePortalConfig();
  const dates = usePortalDates();
  const [filter, setFilter] = useState<Filter>("open");
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [page, setPage] = useState(1);

  useEffect(() => {
    const t = setTimeout(() => {
      setDebounced(search);
      setPage(1);
    }, 250);
    return () => clearTimeout(t);
  }, [search]);

  const list = useQuery({
    queryKey: ["portal", "tickets", filter, debounced, page],
    queryFn: () => portalTicketApi.list(filter, debounced, page),
    placeholderData: keepPreviousData,
  });

  const data = list.data;
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const companyScope = data?.scope === "company" || me.user?.canSeeCompanyTickets;
  const newTicketEnabled = config.data?.enabled ? config.data.newTicketEnabled : false;

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="font-display text-display-sm tracking-tight">{companyScope ? "Tickets" : "My tickets"}</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            {companyScope
              ? `Every ticket for ${me.user?.companyName ?? "your company"}, including those opened by colleagues.`
              : "The requests you opened with the service desk."}
          </p>
        </div>
        {newTicketEnabled && (
          <Button className="gap-2" onClick={() => navigate({ to: "/portal/tickets/new" })}>
            <Plus className="h-4 w-4" />
            New ticket
          </Button>
        )}
      </div>

      <div className="glass-card overflow-hidden">
        <div className="flex flex-wrap items-center gap-3 border-b border-glass px-4 py-3">
          <div className="inline-flex rounded-lg border border-glass bg-glass p-0.5" role="tablist">
            {FILTERS.map((f) => (
              <button
                key={f.key}
                role="tab"
                aria-selected={filter === f.key}
                type="button"
                onClick={() => {
                  setFilter(f.key);
                  setPage(1);
                }}
                className={cn(
                  "rounded-md px-3 py-1 text-xs font-medium transition-colors",
                  filter === f.key ? "bg-glass-strong text-foreground shadow-sm" : "text-muted-foreground hover:text-foreground",
                )}
              >
                {f.label}
              </button>
            ))}
          </div>
          <div className="relative ml-auto w-full sm:w-72">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/70" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search by subject or number"
              className="pl-9"
              aria-label="Search tickets"
            />
          </div>
        </div>

        {list.isLoading ? (
          <div className="space-y-2 p-4">
            {Array.from({ length: 6 }).map((_, i) => (
              <div key={i} className="h-12 animate-pulse rounded-md bg-glass" />
            ))}
          </div>
        ) : list.isError ? (
          <div className="p-8 text-center text-sm text-destructive/90">Could not load your tickets. Try again in a moment.</div>
        ) : !data || data.items.length === 0 ? (
          <div className="flex flex-col items-center gap-2 px-4 py-14 text-center">
            <Inbox className="h-8 w-8 text-muted-foreground/60" />
            <p className="text-sm font-medium">No {filter === "all" ? "" : filter} tickets{debounced ? ` matching “${debounced}”` : ""}.</p>
            {newTicketEnabled && filter !== "closed" && !debounced ? (
              <p className="text-xs text-muted-foreground">
                Need help?{" "}
                <Link to="/portal/tickets/new" className="font-medium text-primary hover:underline">
                  Open a new ticket
                </Link>
                .
              </p>
            ) : null}
          </div>
        ) : (
          <table className="w-full text-sm" data-testid="portal-ticket-table">
            <thead className="sd-table-head text-left text-[11px] uppercase tracking-[0.14em] text-muted-foreground">
              <tr className="border-b border-glass">
                <th className="px-4 py-2.5 font-medium">Ticket</th>
                <th className="hidden px-4 py-2.5 font-medium md:table-cell">Status</th>
                {companyScope ? <th className="hidden px-4 py-2.5 font-medium lg:table-cell">Requester</th> : null}
                <th className="hidden px-4 py-2.5 font-medium md:table-cell">Priority</th>
                <th className="px-4 py-2.5 text-right font-medium">Updated</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((t) => (
                <tr
                  key={t.id}
                  className="cursor-pointer border-b border-glass transition-colors last:border-0 hover:bg-glass-hover"
                  onClick={() => navigate({ to: "/portal/tickets/$ticketId", params: { ticketId: t.id } })}
                >
                  <td className="px-4 py-3">
                    <div className="flex items-start gap-3">
                      <span className="mt-0.5 font-mono text-[11px] text-muted-foreground">#{t.number}</span>
                      <div className="min-w-0">
                        <div className="truncate font-medium text-foreground">{t.subject}</div>
                        <div className="mt-0.5 flex items-center gap-2 text-[11px] text-muted-foreground md:hidden">
                          <StatusPill name={t.status.name} color={t.status.color} />
                          {companyScope ? <span>{t.requester.isYou ? "You" : t.requester.name || t.requester.email}</span> : null}
                        </div>
                      </div>
                    </div>
                  </td>
                  <td className="hidden px-4 py-3 md:table-cell">
                    <StatusPill name={t.status.name} color={t.status.color} />
                  </td>
                  {companyScope ? (
                    <td className="hidden px-4 py-3 text-muted-foreground lg:table-cell">
                      {t.requester.isYou ? "You" : t.requester.name || t.requester.email}
                    </td>
                  ) : null}
                  <td className="hidden px-4 py-3 md:table-cell">
                    <PriorityDot name={t.priority.name} color={t.priority.color} />
                  </td>
                  <td className="whitespace-nowrap px-4 py-3 text-right text-xs text-muted-foreground">{dates.dateTime(t.updatedUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {data && data.total > data.pageSize && (
          <div className="flex items-center justify-between border-t border-glass px-4 py-2.5 text-xs text-muted-foreground">
            <span>
              {data.total} ticket{data.total === 1 ? "" : "s"} · page {data.page} of {totalPages}
            </span>
            <div className="flex items-center gap-1">
              <Button variant="ghost" size="sm" disabled={page <= 1} onClick={() => setPage((p) => Math.max(1, p - 1))} aria-label="Previous page">
                <ChevronLeft className="h-4 w-4" />
              </Button>
              <Button variant="ghost" size="sm" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)} aria-label="Next page">
                <ChevronRight className="h-4 w-4" />
              </Button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
