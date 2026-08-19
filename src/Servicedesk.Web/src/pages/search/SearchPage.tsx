import { useEffect, useMemo, useState } from "react";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { searchApi, type SearchHit, type SearchSort } from "@/lib/api";
import { sanitizeSnippet } from "@/lib/sanitize";
import { KIND_ORDER, labelForKind, hitHref } from "@/components/search/searchMeta";
import { useTheme } from "@/app/ThemeProvider";
import { useServerTime, toServerLocalDate } from "@/hooks/useServerTime";

const PAGE_SIZE = 25;

const SORT_OPTIONS: ReadonlyArray<{ value: SearchSort; label: string; ticketsOnly?: boolean }> = [
  { value: "relevance", label: "Relevance" },
  { value: "newest", label: "Newest first" },
  { value: "oldest", label: "Oldest first" },
  { value: "status", label: "Open before closed", ticketsOnly: true },
];

function parseSort(raw: string | undefined, type: string): SearchSort {
  const match = SORT_OPTIONS.find((o) => o.value === raw);
  if (!match) return "relevance";
  if (match.ticketsOnly && type !== "tickets") return "relevance";
  return match.value;
}

/// Full-page search. Tabs are derived from the server-provided
/// availableKinds so a non-admin never even sees the Settings tab. URL
/// search params (q, type, offset, sort) are the source of truth — deep
/// links, back/forward, and refresh all work the natural way.
export function SearchPage() {
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as {
    q?: string;
    type?: string;
    offset?: number;
    sort?: string;
  };

  const q = (search.q ?? "").trim();
  const [input, setInput] = useState(q);
  const activeType = search.type ?? "tickets";
  const offset = Math.max(0, Number(search.offset ?? 0));
  const activeSort = parseSort(search.sort, activeType);

  useEffect(() => setInput(q), [q]);

  const { data, isFetching } = useQuery({
    queryKey: ["search", "full", q, activeType, offset, activeSort],
    queryFn: () => searchApi.full(q, activeType, PAGE_SIZE, offset, activeSort),
    enabled: q.length > 0,
    staleTime: 10_000,
  });

  // The category dropdown lives in the search bar. Options come from the
  // server-provided availableKinds so a non-admin never sees a tab (e.g.
  // Settings) they aren't authorized for. Falls back to the active type
  // until the first response lands, so the dropdown is never empty.
  const typeOptions = useMemo(() => {
    const kinds = (data?.availableKinds ?? [])
      .slice()
      .sort((a, b) => KIND_ORDER.indexOf(a) - KIND_ORDER.indexOf(b));
    return kinds.length > 0 ? kinds : [activeType];
  }, [data?.availableKinds, activeType]);

  function updateUrl(next: { q?: string; type?: string; offset?: number; sort?: SearchSort }) {
    const nextType = next.type ?? activeType;
    // A tickets-only sort silently resets when the user switches category.
    const nextSort = parseSort(next.sort ?? activeSort, nextType);
    navigate({
      to: "/search",
      search: {
        q: next.q ?? q,
        type: nextType,
        offset: next.offset,
        sort: nextSort === "relevance" ? undefined : nextSort,
      },
    });
  }

  const group = data?.group;
  const total = group?.totalInGroup ?? 0;
  const pageStart = total === 0 ? 0 : offset + 1;
  const pageEnd = Math.min(total, offset + (group?.hits.length ?? 0));

  return (
    <div className="mx-auto w-full max-w-5xl py-6">
      <h1 className="font-display text-display-sm font-semibold">Search results</h1>

      <div className="mt-4 flex flex-col gap-3 sm:flex-row sm:items-stretch">
        <form
          className="flex-1"
          onSubmit={(e) => {
            e.preventDefault();
            updateUrl({ q: input.trim(), offset: 0 });
          }}
        >
          <input
            autoFocus
            value={input}
            onChange={(e) => setInput(e.target.value)}
            placeholder="What are you looking for?"
            className="w-full rounded-xl border border-glass bg-glass px-4 py-3 text-base outline-none ring-1 ring-inset ring-white/5 focus:ring-white/20"
          />
        </form>

        <select
          value={activeType}
          onChange={(e) => updateUrl({ type: e.target.value, offset: 0 })}
          aria-label="Search in"
          className="rounded-xl border border-glass bg-glass px-4 py-3 text-base text-foreground outline-none ring-1 ring-inset ring-white/5 focus:ring-white/20 cursor-pointer sm:w-56"
        >
          {typeOptions.map((t) => (
            <option key={t} value={t} className="bg-background">
              {labelForKind(t)}
            </option>
          ))}
        </select>

        <select
          value={activeSort}
          onChange={(e) => updateUrl({ sort: e.target.value as SearchSort, offset: 0 })}
          aria-label="Sort by"
          className="rounded-xl border border-glass bg-glass px-4 py-3 text-base text-foreground outline-none ring-1 ring-inset ring-white/5 focus:ring-white/20 cursor-pointer sm:w-48"
        >
          {SORT_OPTIONS.filter((o) => !o.ticketsOnly || activeType === "tickets").map((o) => (
            <option key={o.value} value={o.value} className="bg-background">
              {o.label}
            </option>
          ))}
        </select>
      </div>

      <div className="mt-4">
        {q.length === 0 && (
          <p className="py-12 text-center text-muted-foreground">
            Type above to search.
          </p>
        )}

        {q.length > 0 && isFetching && !group && (
          <p className="py-12 text-center text-muted-foreground">Searching…</p>
        )}

        {q.length > 0 && group && (
          <>
            <div className="mb-3 text-xs text-muted-foreground">
              {total === 0
                ? `No results for "${q}".`
                : `${pageStart}–${pageEnd} of ${total} results`}
            </div>

            <ul className="divide-y divide-glass rounded-xl border border-glass bg-glass">
              {group.hits.map((hit) => (
                <HitRow key={`${hit.kind}:${hit.entityId}`} hit={hit} query={q} />
              ))}
            </ul>

            {(offset > 0 || group.hasMore) && (
              <div className="mt-4 flex items-center justify-between text-sm">
                <button
                  disabled={offset === 0}
                  onClick={() => updateUrl({ offset: Math.max(0, offset - PAGE_SIZE) })}
                  className="rounded-md border border-glass px-3 py-1 disabled:opacity-40"
                >
                  ← Previous
                </button>
                <button
                  disabled={!group.hasMore}
                  onClick={() => updateUrl({ offset: offset + PAGE_SIZE })}
                  className="rounded-md border border-glass px-3 py-1 disabled:opacity-40"
                >
                  Next →
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}

function HitRow({ hit, query }: { hit: SearchHit; query: string }) {
  const navigate = useNavigate();
  const { theme } = useTheme();
  // Server offset for the created/closed dates on ticket hits — display
  // only, the timestamps themselves are server-provided.
  const { time: serverTime } = useServerTime();
  const serverOffset = serverTime?.offsetMinutes ?? 0;
  const requester = hit.meta?.requester;
  const company = hit.meta?.company;
  const subtitle = [requester, company].filter(Boolean).join(" · ");
  const statusName = hit.meta?.statusName;
  const statusColor = hit.meta?.statusColor;
  const statusPillStyle =
    statusColor
      ? theme === "light"
        ? {
            backgroundColor: `color-mix(in srgb, ${statusColor} 22%, transparent)`,
            color: `color-mix(in srgb, ${statusColor}, black 45%)`,
            borderColor: `color-mix(in srgb, ${statusColor} 40%, transparent)`,
          }
        : {
            backgroundColor: `${statusColor}20`,
            color: statusColor,
            borderColor: `${statusColor}55`,
          }
      : undefined;

  function go() {
    if (hit.kind === "tickets") {
      navigate({
        to: hitHref(hit) as string,
        search: { from: "search", q: query } as never,
      });
    } else {
      navigate({ to: hitHref(hit) as string });
    }
  }

  return (
    <li
      className="cursor-pointer px-4 py-3 transition-colors hover:bg-glass-hover"
      onClick={go}
    >
      <div className="flex items-baseline gap-2">
        {hit.kind === "tickets" && statusColor && (
          <span
            className="h-2 w-2 shrink-0 self-center rounded-full"
            style={{ backgroundColor: statusColor }}
            title={statusName ?? undefined}
            aria-label={statusName ?? undefined}
          />
        )}
        <span className="truncate font-medium">{hit.title}</span>
        {hit.kind === "tickets" && statusName && (
          <span
            className="shrink-0 rounded-full border px-2 py-0.5 text-[10px] font-medium uppercase tracking-wider"
            style={statusPillStyle}
          >
            {statusName}
          </span>
        )}
        {subtitle && (
          <span className="shrink-0 text-xs text-muted-foreground">{subtitle}</span>
        )}
      </div>
      {hit.snippet && (
        <div
          className="mt-1 truncate text-xs text-muted-foreground"
          dangerouslySetInnerHTML={{ __html: sanitizeSnippet(hit.snippet) }}
        />
      )}
      {hit.kind === "tickets" && hit.meta?.createdUtc && (
        <div className="mt-0.5 text-[10px] text-muted-foreground/70">
          Created {toServerLocalDate(hit.meta.createdUtc, serverOffset)}
          {hit.meta?.closedUtc
            ? ` · Closed ${toServerLocalDate(hit.meta.closedUtc, serverOffset)}`
            : ""}
        </div>
      )}
    </li>
  );
}
