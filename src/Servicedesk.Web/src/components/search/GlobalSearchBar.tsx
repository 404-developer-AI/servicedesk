import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Command } from "cmdk";
import { useNavigate } from "@tanstack/react-router";
import { Search } from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { searchApi, type SearchHit } from "@/lib/api";
import { sanitizeSnippet } from "@/lib/sanitize";
import { cn } from "@/lib/utils";
import { useAuth } from "@/auth/authStore";
import { KIND_ORDER, labelForKind, hitHref } from "@/components/search/searchMeta";

const DEBOUNCE_MS = 150;

/// Sidebar search input + cmdk-powered dropdown. Hidden for Customer until
/// the customer portal ships. Pressing Ctrl+K from anywhere opens it.
export function GlobalSearchBar({ collapsed = false }: { collapsed?: boolean }) {
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement | null>(null);
  // Anchor can be either the inline input wrapper (expanded) or the
  // icon-only trigger button (collapsed) — both contribute a bounding
  // rect to position the portal-mounted result panel.
  const anchorRef = useRef<HTMLElement | null>(null);
  const { user } = useAuth();

  const [open, setOpen] = useState(false);
  const [value, setValue] = useState("");
  const [debounced, setDebounced] = useState("");
  const [anchorRect, setAnchorRect] = useState<{ left: number; top: number; width: number } | null>(null);

  // Debounce the query so we don't flood the backend on every keystroke.
  useEffect(() => {
    const t = setTimeout(() => setDebounced(value), DEBOUNCE_MS);
    return () => clearTimeout(t);
  }, [value]);

  // The dropdown lives in a portal so it can extend past the sidebar's
  // `overflow-hidden`, which would otherwise clip it to the sidebar width.
  // Recompute its position whenever the panel opens or the layout shifts.
  useLayoutEffect(() => {
    if (!open) return;
    const DROPDOWN_WIDTH = 640;
    const GAP = 6;
    const SCREEN_PADDING = 12;
    const update = () => {
      const el = anchorRef.current;
      if (!el) return;
      const rect = el.getBoundingClientRect();
      const desiredWidth = Math.min(DROPDOWN_WIDTH, window.innerWidth - SCREEN_PADDING * 2);
      let left = rect.left;
      if (left + desiredWidth > window.innerWidth - SCREEN_PADDING) {
        left = Math.max(SCREEN_PADDING, window.innerWidth - SCREEN_PADDING - desiredWidth);
      }
      setAnchorRect({ left, top: rect.bottom + GAP, width: desiredWidth });
    };
    update();
    window.addEventListener("resize", update);
    window.addEventListener("scroll", update, true);
    return () => {
      window.removeEventListener("resize", update);
      window.removeEventListener("scroll", update, true);
    };
  }, [open]);

  // Global Ctrl+K / Cmd+K.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setOpen(true);
        // When collapsed, the input only mounts after `open` is true, so
        // defer focus until the portal has committed.
        setTimeout(() => inputRef.current?.focus(), 0);
      } else if (e.key === "Escape" && open) {
        setOpen(false);
        inputRef.current?.blur();
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [open]);

  const canSearch =
    (user?.role === "Agent" || user?.role === "Admin")
    && user?.searchEnabled === true;

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
    // Default to the tickets category when the user can see it; otherwise
    // fall back to the first kind they're authorized for.
    const kinds = data?.availableKinds ?? [];
    const defaultKind = kinds.includes("tickets") ? "tickets" : kinds[0] ?? "tickets";
    resetAndNavigate("/search", { q: query, type: defaultKind, offset: undefined });
  }

  function goHit(hit: SearchHit) {
    // Only ticket hits get the search-context pill — for contacts / companies
    // / KB / settings the navigation goes to a richer destination already.
    if (hit.kind === "tickets") {
      resetAndNavigate(hitHref(hit), { from: "search", q: query });
    } else {
      resetAndNavigate(hitHref(hit));
    }
  }

  const groupsOrdered = (data?.groups ?? [])
    .slice()
    .sort((a, b) => KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind));

  return (
    <div
      className={cn(collapsed ? "relative" : "relative w-full")}
      data-testid="global-search"
    >
      <Command shouldFilter={false} className="relative">
        {collapsed ? (
          <button
            ref={(el) => { anchorRef.current = el; }}
            type="button"
            onMouseDown={(e) => {
              // Prevent the input inside the portal from losing focus when
              // the user clicks the trigger to toggle it — avoids the
              // close-then-reopen race the blur timer would otherwise cause.
              if (open) e.preventDefault();
            }}
            onClick={() => {
              if (open) {
                setOpen(false);
              } else {
                setOpen(true);
                setTimeout(() => inputRef.current?.focus(), 0);
              }
            }}
            title="Search (Ctrl+K)"
            className="flex h-9 w-9 items-center justify-center rounded-lg border border-glass bg-glass text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
          >
            <Search className="h-4 w-4" />
          </button>
        ) : (
          <div
            ref={(el) => { anchorRef.current = el; }}
            className={cn(
              "flex items-center gap-2 rounded-lg border border-glass bg-glass px-3 py-2",
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
            <kbd className="shrink-0 rounded border border-glass px-1.5 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
              ⌘K
            </kbd>
          </div>
        )}

        {open && anchorRect && createPortal(
          <div
            style={{
              position: "fixed",
              left: anchorRect.left,
              top: anchorRect.top,
              width: anchorRect.width,
            }}
            className={cn(
              "z-50 overflow-hidden rounded-xl border border-glass bg-background/95 shadow-2xl backdrop-blur",
              "ring-1 ring-inset ring-white/5",
            )}
            onMouseDown={(e) => e.preventDefault()}
          >
            {collapsed && (
              <div
                className="border-b border-glass p-2"
                onMouseDown={(e) => e.stopPropagation()}
              >
                <div className="flex items-center gap-2 rounded-lg border border-glass bg-glass px-3 py-2 ring-1 ring-inset ring-white/5 transition focus-within:ring-white/20">
                  <Search className="h-4 w-4 shrink-0 text-muted-foreground" />
                  <Command.Input
                    ref={inputRef}
                    value={value}
                    onValueChange={setValue}
                    onBlur={() => {
                      setTimeout(() => setOpen(false), 120);
                    }}
                    placeholder="Zoeken…"
                    className="flex-1 min-w-0 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
                  />
                </div>
              </div>
            )}
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
                    className="flex cursor-pointer items-center justify-between rounded-md px-3 py-2 text-sm font-medium aria-selected:bg-glass-strong"
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
                          const statusName = hit.meta?.statusName;
                          const statusColor = hit.meta?.statusColor;
                          return (
                            <Command.Item
                              key={`${hit.kind}:${hit.entityId}`}
                              value={`${hit.kind}:${hit.entityId}`}
                              onSelect={() => goHit(hit)}
                              className="flex cursor-pointer flex-col gap-0.5 rounded-md px-3 py-2 text-sm aria-selected:bg-glass-strong"
                            >
                              <div className="flex items-baseline gap-2">
                                {hit.kind === "tickets" && statusColor && (
                                  <span
                                    className="mt-0.5 h-2 w-2 shrink-0 self-center rounded-full"
                                    style={{ backgroundColor: statusColor }}
                                    title={statusName ?? undefined}
                                    aria-label={statusName ?? undefined}
                                  />
                                )}
                                <span className="truncate font-medium">{hit.title}</span>
                                {subtitle && (
                                  <span className="shrink-0 text-xs text-muted-foreground">{subtitle}</span>
                                )}
                              </div>
                              {hit.snippet && (
                                <span
                                  className="truncate text-xs text-muted-foreground"
                                  dangerouslySetInnerHTML={{ __html: sanitizeSnippet(hit.snippet) }}
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
          </div>,
          document.body,
        )}
      </Command>
    </div>
  );
}
