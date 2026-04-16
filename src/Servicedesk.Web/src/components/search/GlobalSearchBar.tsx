import { useEffect, useRef, useState } from "react";
import { Command } from "cmdk";
import { useNavigate } from "@tanstack/react-router";
import { Search } from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { searchApi, type SearchHit } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useAuth } from "@/auth/authStore";
import { KIND_ORDER, labelForKind, hitHref } from "@/components/search/searchMeta";

const DEBOUNCE_MS = 150;

/// Sidebar search input + cmdk-powered dropdown. Hidden for Customer until
/// the customer portal ships. Pressing Ctrl+K from anywhere opens it.
export function GlobalSearchBar({ collapsed = false }: { collapsed?: boolean }) {
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement | null>(null);
  const { user } = useAuth();

  const [open, setOpen] = useState(false);
  const [value, setValue] = useState("");
  const [debounced, setDebounced] = useState("");

  // Debounce the query so we don't flood the backend on every keystroke.
  useEffect(() => {
    const t = setTimeout(() => setDebounced(value), DEBOUNCE_MS);
    return () => clearTimeout(t);
  }, [value]);

  // Global Ctrl+K / Cmd+K.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        inputRef.current?.focus();
        setOpen(true);
      } else if (e.key === "Escape" && open) {
        setOpen(false);
        inputRef.current?.blur();
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [open]);

  const canSearch = user?.role === "Agent" || user?.role === "Admin";

  const { data, isFetching } = useQuery({
    queryKey: ["search", "quick", debounced],
    queryFn: () => searchApi.quick(debounced, 6),
    enabled: canSearch && debounced.trim().length > 0,
    staleTime: 10_000,
  });

  const minLen = data?.minQueryLength ?? 3;
  const query = value.trim();
  const showResults = open && query.length >= minLen;
  const showHint = open && query.length > 0 && query.length < minLen;

  if (!canSearch) return null;

  function resetAndNavigate(to: string, search?: Record<string, unknown>) {
    inputRef.current?.blur();
    setValue("");
    setOpen(false);
    navigate({ to, search } as never);
  }

  function goFullPage() {
    const firstKind = data?.availableKinds?.[0] ?? "tickets";
    resetAndNavigate("/search", { q: query, type: firstKind, offset: undefined });
  }

  function goHit(hit: SearchHit) {
    resetAndNavigate(hitHref(hit));
  }

  const groupsOrdered = (data?.groups ?? [])
    .slice()
    .sort((a, b) => KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind));

  // Collapsed: icon-only button that opens the search via Ctrl+K focus
  if (collapsed) {
    return (
      <button
        type="button"
        onClick={() => {
          inputRef.current?.focus();
          setOpen(true);
        }}
        title="Search (Ctrl+K)"
        className="flex h-9 w-9 items-center justify-center rounded-lg border border-white/10 bg-white/[0.03] text-muted-foreground transition-colors hover:bg-white/[0.06] hover:text-foreground"
      >
        <Search className="h-4 w-4" />
      </button>
    );
  }

  return (
    <div className="relative w-full" data-testid="global-search">
      <Command shouldFilter={false} className="relative">
        <div
          className={cn(
            "flex items-center gap-2 rounded-lg border border-white/10 bg-white/5 px-3 py-2",
            "ring-1 ring-inset ring-white/5 transition focus-within:ring-white/20",
          )}
        >
          <Search className="h-4 w-4 shrink-0 text-muted-foreground" />
          <Command.Input
            ref={inputRef}
            value={value}
            onValueChange={setValue}
            onFocus={() => setOpen(true)}
            onBlur={() => {
              setTimeout(() => setOpen(false), 120);
            }}
            placeholder="Zoeken…"
            className="flex-1 min-w-0 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
          />
          <kbd className="shrink-0 rounded border border-white/10 px-1.5 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
            ⌘K
          </kbd>
        </div>

        {open && (
          <div
            className={cn(
              "absolute left-0 top-[calc(100%+6px)] z-50 w-[min(480px,90vw)] overflow-hidden rounded-xl border border-white/10 bg-background/95 shadow-2xl backdrop-blur",
              "ring-1 ring-inset ring-white/5",
            )}
            onMouseDown={(e) => e.preventDefault()}
          >
            <Command.List className="max-h-[70vh] overflow-y-auto p-1">
              {showHint && (
                <div className="px-3 py-2 text-xs text-muted-foreground">
                  Type nog {minLen - query.length} karakter{minLen - query.length === 1 ? "" : "s"}…
                </div>
              )}

              {showResults && (
                <>
                  <Command.Item
                    value="__show-details__"
                    onSelect={goFullPage}
                    className="flex cursor-pointer items-center justify-between rounded-md px-3 py-2 text-sm font-medium aria-selected:bg-white/10"
                  >
                    <span>Toon zoekdetails →</span>
                    <span className="text-xs text-muted-foreground">alle resultaten</span>
                  </Command.Item>

                  {isFetching && (
                    <div className="px-3 py-2 text-xs text-muted-foreground">Zoeken…</div>
                  )}

                  {!isFetching && groupsOrdered.every((g) => g.hits.length === 0) && (
                    <div className="px-3 py-6 text-center text-sm text-muted-foreground">
                      Niets gevonden voor "{query}".
                    </div>
                  )}

                  {groupsOrdered.map((group) =>
                    group.hits.length === 0 ? null : (
                      <Command.Group
                        key={group.kind}
                        heading={
                          <div className="px-3 pb-1 pt-2 text-[11px] uppercase tracking-[0.18em] text-muted-foreground">
                            {labelForKind(group.kind)}
                            {group.hasMore && (
                              <span className="ml-2 text-muted-foreground/70">
                                +{group.totalInGroup - group.hits.length} meer
                              </span>
                            )}
                          </div>
                        }
                      >
                        {group.hits.map((hit) => {
                          const requester = hit.meta?.requester;
                          const company = hit.meta?.company;
                          const subtitle = [requester, company].filter(Boolean).join(" · ");
                          return (
                            <Command.Item
                              key={`${hit.kind}:${hit.entityId}`}
                              value={`${hit.kind}:${hit.entityId}`}
                              onSelect={() => goHit(hit)}
                              className="flex cursor-pointer flex-col gap-0.5 rounded-md px-3 py-2 text-sm aria-selected:bg-white/10"
                            >
                              <div className="flex items-baseline gap-2">
                                <span className="truncate font-medium">{hit.title}</span>
                                {subtitle && (
                                  <span className="shrink-0 text-xs text-muted-foreground">{subtitle}</span>
                                )}
                              </div>
                              {hit.snippet && (
                                <span
                                  className="truncate text-xs text-muted-foreground"
                                  dangerouslySetInnerHTML={{ __html: hit.snippet }}
                                />
                              )}
                            </Command.Item>
                          );
                        })}
                      </Command.Group>
                    ),
                  )}
                </>
              )}
            </Command.List>
          </div>
        )}
      </Command>
    </div>
  );
}
