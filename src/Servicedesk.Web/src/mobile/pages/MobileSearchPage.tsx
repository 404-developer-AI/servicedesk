import * as React from "react";
import { Link, useNavigate } from "@tanstack/react-router";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { ExternalLink, Loader2, Search, X } from "lucide-react";
import { searchApi, type SearchHit } from "@/lib/api";
import { hitHref, labelForKind, KIND_ORDER } from "@/components/search/searchMeta";
import { sanitizeSnippet } from "@/lib/sanitize";
import { useMobileHeader } from "../mobileHeader";

/// `/m/search` — the global search (same endpoints, same row-level
/// authorization as desktop: every source filters to what this agent may
/// see). One field; results grouped by kind; "Show all" pages one kind.
/// Ticket hits open the mobile ticket page; other kinds open their desktop
/// page (no mobile view for those in this phase).
export function MobileSearchPage({ initialQuery }: { initialQuery?: string }) {
  useMobileHeader("Search");
  const navigate = useNavigate();
  const [term, setTerm] = React.useState(initialQuery ?? "");
  const [kind, setKind] = React.useState<string | null>(null);
  const [debounced, setDebounced] = React.useState(term.trim());

  // Probe once for the server's min length + debounce (same knobs as the
  // desktop search bar).
  const [knobs, setKnobs] = React.useState({ min: 2, debounceMs: 250 });
  React.useEffect(() => {
    const id = window.setTimeout(() => setDebounced(term.trim()), knobs.debounceMs);
    return () => window.clearTimeout(id);
  }, [term, knobs.debounceMs]);

  // Keep the query in the URL so back/forward and a reload keep the search.
  React.useEffect(() => {
    void navigate({ to: "/m/search", search: debounced ? { q: debounced } : {}, replace: true });
  }, [debounced, navigate]);

  const ready = debounced.length >= knobs.min;
  const quick = useQuery({
    queryKey: ["search", "mobile", "quick", debounced],
    queryFn: () => searchApi.quick(debounced, 8),
    enabled: ready && kind === null,
    staleTime: 30_000,
    placeholderData: (prev) => prev,
  });
  React.useEffect(() => {
    if (quick.data) setKnobs({ min: quick.data.minQueryLength, debounceMs: quick.data.debounceMs });
  }, [quick.data]);

  const full = useInfiniteQuery({
    queryKey: ["search", "mobile", "full", kind, debounced],
    enabled: ready && kind !== null,
    initialPageParam: 0,
    queryFn: ({ pageParam }) => searchApi.full(debounced, kind!, 25, pageParam),
    getNextPageParam: (last, pages) => (last.group.hasMore ? pages.length * 25 : undefined),
    staleTime: 30_000,
  });

  const groups = React.useMemo(() => {
    const g = quick.data?.groups.filter((x) => x.hits.length > 0) ?? [];
    return [...g].sort((a, b) => order(a.kind) - order(b.kind));
  }, [quick.data]);

  return (
    <div className="space-y-4">
      <div className="relative">
        <Search className="pointer-events-none absolute left-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
        <input
          type="search"
          enterKeyHint="search"
          autoFocus={!initialQuery}
          value={term}
          onChange={(e) => setTerm(e.target.value)}
          placeholder="Tickets, contacts, companies…"
          aria-label="Search"
          className="sd-m-input h-12 w-full rounded-xl border border-glass bg-glass pl-10 pr-10 text-base outline-hidden placeholder:text-muted-foreground"
        />
        {term && (
          <button
            type="button"
            onClick={() => {
              setTerm("");
              setKind(null);
            }}
            className="absolute right-1 top-1/2 inline-flex h-10 w-10 -translate-y-1/2 items-center justify-center text-muted-foreground"
            aria-label="Clear search"
          >
            <X className="h-4 w-4" />
          </button>
        )}
      </div>

      {kind !== null && (
        <button
          type="button"
          onClick={() => setKind(null)}
          className="h-9 rounded-full border border-glass bg-glass px-3.5 text-[13px] font-medium text-muted-foreground"
        >
          ← All results
        </button>
      )}

      {!ready ? (
        <p className="py-8 text-center text-sm text-muted-foreground">
          {term.trim().length === 0 ? "Search by ticket number, subject, contact or company." : `Type at least ${knobs.min} characters.`}
        </p>
      ) : kind === null ? (
        quick.isLoading ? (
          <Spinner />
        ) : groups.length === 0 ? (
          <p className="py-8 text-center text-sm text-muted-foreground">No results.</p>
        ) : (
          groups.map((g) => (
            <section key={g.kind} className="space-y-2">
              <div className="flex items-center justify-between px-1">
                <h2 className="text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">
                  {labelForKind(g.kind)} <span className="font-normal">· {g.totalInGroup}{g.partial ? "+" : ""}</span>
                </h2>
                {g.hasMore && (
                  <button type="button" onClick={() => setKind(g.kind)} className="h-9 px-2 text-[13px] font-medium text-primary">
                    Show all
                  </button>
                )}
              </div>
              <HitList hits={g.hits} />
            </section>
          ))
        )
      ) : full.isLoading ? (
        <Spinner />
      ) : (
        <section className="space-y-2">
          <h2 className="px-1 text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">{labelForKind(kind)}</h2>
          <HitList hits={(full.data?.pages ?? []).flatMap((p) => p.group.hits)} />
          {full.hasNextPage && (
            <button
              type="button"
              onClick={() => void full.fetchNextPage()}
              disabled={full.isFetchingNextPage}
              className="flex h-12 w-full items-center justify-center gap-2 rounded-xl border border-glass bg-glass text-[14px] font-medium"
            >
              {full.isFetchingNextPage && <Loader2 className="h-4 w-4 animate-spin" />}
              Load more
            </button>
          )}
        </section>
      )}
    </div>
  );
}

function order(kind: string): number {
  const i = KIND_ORDER.indexOf(kind);
  return i < 0 ? 999 : i;
}

function Spinner() {
  return (
    <div className="flex justify-center py-8 text-muted-foreground">
      <Loader2 className="h-5 w-5 animate-spin" />
    </div>
  );
}

/// Ticket-ish hits (tickets, and kinds that live on a ticket) map to the
/// mobile ticket page; everything else keeps its desktop target.
function mobileTarget(hit: SearchHit): { ticketId: string; hash?: string } | { href: string } {
  const href = hitHref(hit);
  const m = /^\/tickets\/([^/?#]+)(?:[?][^#]*)?(?:#(event-\d+))?$/.exec(href);
  if (m) return { ticketId: m[1], hash: m[2] };
  return { href };
}

/// Snippets are server ts_headline output, sanitised to <b> only. The
/// wrapper object is memoised so a parent re-render (typing) never resets
/// innerHTML.
const Snippet = React.memo(function Snippet({ html }: { html: string | null }) {
  const danger = React.useMemo(() => ({ __html: sanitizeSnippet(html) }), [html]);
  if (!danger.__html) return null;
  return (
    <p
      className="mt-0.5 line-clamp-2 text-[13px] text-muted-foreground [&_b]:font-semibold [&_b]:text-foreground"
      dangerouslySetInnerHTML={danger}
    />
  );
});

function HitList({ hits }: { hits: SearchHit[] }) {
  return (
    <ul className="glass-card divide-y divide-glass overflow-hidden">
      {hits.map((hit) => {
        const target = mobileTarget(hit);
        const isTicket = "ticketId" in target;
        const inner = (
          <>
            <div className="flex items-center gap-2">
              <span className="min-w-0 flex-1 truncate text-[15px] font-medium">{hit.title}</span>
              {!isTicket && <ExternalLink className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-label="Opens the desktop page" />}
            </div>
            <Snippet html={hit.snippet} />
          </>
        );
        return (
          <li key={`${hit.kind}:${hit.entityId}`}>
            {isTicket ? (
              <Link
                to="/m/tickets/$ticketId"
                params={{ ticketId: target.ticketId }}
                hash={target.hash}
                className="block px-4 py-3 active:bg-glass-hover"
              >
                {inner}
              </Link>
            ) : target.href === "#" ? (
              <div className="px-4 py-3 opacity-70">{inner}</div>
            ) : (
              <a href={target.href} className="block px-4 py-3 active:bg-glass-hover">
                {inner}
              </a>
            )}
          </li>
        );
      })}
    </ul>
  );
}
