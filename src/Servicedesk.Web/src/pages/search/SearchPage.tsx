import { useEffect, useMemo, useState } from "react";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { searchApi, type SearchHit } from "@/lib/api";
import { sanitizeSnippet } from "@/lib/sanitize";
import { KIND_ORDER, labelForKind, hitHref } from "@/components/search/searchMeta";
import { cn } from "@/lib/utils";
import { useTheme } from "@/app/ThemeProvider";

const PAGE_SIZE = 25;

/// Full-page search. Tabs are derived from the server-provided
/// availableKinds so a non-admin never even sees the Settings tab. URL
/// search params (q, type, offset) are the source of truth — deep links,
/// back/forward, and refresh all work the natural way.
export function SearchPage() {
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as {
    q?: string;
    type?: string;
    offset?: number;
  };

  const q = (search.q ?? "").trim();
  const [input, setInput] = useState(q);
  const activeType = search.type ?? "tickets";
  const offset = Math.max(0, Number(search.offset ?? 0));

  useEffect(() => setInput(q), [q]);

  const { data, isFetching } = useQuery({
    queryKey: ["search", "full", q, activeType, offset],
    queryFn: () => searchApi.full(q, activeType, PAGE_SIZE, offset),
    enabled: q.length > 0,
    staleTime: 10_000,
  });

  const tabs = useMemo(() => {
    const kinds = data?.availableKinds ?? [];
    return kinds
      .slice()
      .sort((a, b) => KIND_ORDER.indexOf(a) - KIND_ORDER.indexOf(b));
  }, [data?.availableKinds]);

  function updateUrl(next: { q?: string; type?: string; offset?: number }) {
    navigate({
      to: "/search",
      search: {
        q: next.q ?? q,
        type: next.type ?? activeType,
        offset: next.offset,
      },
    });
  }

  const group = data?.group;
  const total = group?.totalInGroup ?? 0;
  const pageStart = total === 0 ? 0 : offset + 1;
  const pageEnd = Math.min(total, offset + (group?.hits.length ?? 0));

  return (
    <div className="mx-auto w-full max-w-5xl py-6">
      <h1 className="font-display text-display-sm font-semibold">Zoekresultaten</h1>

      <form
        className="mt-4"
        onSubmit={(e) => {
          e.preventDefault();
          updateUrl({ q: input.trim(), offset: 0 });
        }}
      >
        <input
          autoFocus
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Waar zoek je naar?"
          className="w-full rounded-xl border border-glass bg-glass px-4 py-3 text-base outline-none ring-1 ring-inset ring-white/5 focus:ring-white/20"
        />
      </form>

      {tabs.length > 0 && (
        <div className="mt-6 flex gap-1 border-b border-glass">
          {tabs.map((t) => {
            const isActive = t === activeType;
            return (
              <button
                key={t}
                onClick={() => updateUrl({ type: t, offset: 0 })}
                className={cn(
                  "px-4 py-2 text-sm transition-colors",
                  isActive
                    ? "border-b-2 border-primary text-foreground"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {labelForKind(t)}
              </button>
            );
          })}
        </div>
      )}

      <div className="mt-4">
        {q.length === 0 && (
          <p className="py-12 text-center text-muted-foreground">
            Type hierboven om te zoeken.
          </p>
        )}

        {q.length > 0 && isFetching && !group && (
          <p className="py-12 text-center text-muted-foreground">Zoeken…</p>
        )}

        {q.length > 0 && group && (
          <>
            <div className="mb-3 text-xs text-muted-foreground">
              {total === 0
                ? `Geen resultaten voor "${q}".`
                : `${pageStart}–${pageEnd} van ${total} resultaten`}
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
                  ← Vorige
                </button>
                <button
                  disabled={!group.hasMore}
                  onClick={() => updateUrl({ offset: offset + PAGE_SIZE })}
                  className="rounded-md border border-glass px-3 py-1 disabled:opacity-40"
                >
                  Volgende →
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
    </li>
  );
}
