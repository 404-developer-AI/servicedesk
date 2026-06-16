import * as React from "react";
import { createPortal } from "react-dom";
import { X } from "lucide-react";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";
import { ticketApi, type TicketPickerItem } from "@/lib/ticket-api";

/// Ticket autocomplete that searches `/api/tickets/picker` and forces a
/// selection (no free-text) — the same behaviour as the Timesheet grids'
/// ticket picker (this is the extracted, reusable version). When `disabled`,
/// renders a muted placeholder instead of the input.
export function TicketAutocomplete({
  value,
  disabled,
  onChange,
  error,
  placeholder = "Search ticket # or subject…",
  disabledLabel = "—",
}: {
  value: TicketPickerItem | null;
  disabled: boolean;
  onChange: (t: TicketPickerItem | null) => void;
  error?: string;
  placeholder?: string;
  disabledLabel?: string;
}) {
  const [query, setQuery] = React.useState("");
  const [open, setOpen] = React.useState(false);
  const [results, setResults] = React.useState<TicketPickerItem[]>([]);
  const [searching, setSearching] = React.useState(false);
  // The dropdown lives in a portal on document.body (see comment below)
  // so its position is computed against the trigger's bounding rect and
  // refreshed on scroll/resize while open.
  const inputRef = React.useRef<HTMLInputElement | null>(null);
  const [anchor, setAnchor] = React.useState<{
    top?: number;
    bottom?: number;
    left: number;
    width: number;
    maxHeight: number;
  } | null>(null);

  React.useEffect(() => {
    if (disabled || !open) return;
    let cancelled = false;
    const handle = window.setTimeout(async () => {
      setSearching(true);
      try {
        const res = await ticketApi.picker(query.trim() || undefined, undefined, 15);
        if (!cancelled) setResults(res.items);
      } catch {
        if (!cancelled) setResults([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 180);
    return () => {
      cancelled = true;
      window.clearTimeout(handle);
    };
  }, [query, open, disabled]);

  // Track the trigger's screen-rect while the dropdown is open so the
  // portal stays glued to it on page scroll / window resize. We render
  // the dropdown via createPortal because a surrounding
  // `overflow-hidden` glass-card clips any descendant that extends past
  // its bounds — portaling to body sidesteps both the clip and stacking
  // oddities from the table / sticky sidebar.
  React.useLayoutEffect(() => {
    if (!open || value) {
      setAnchor(null);
      return;
    }
    const place = () => {
      const el = inputRef.current;
      if (!el) return;
      const r = el.getBoundingClientRect();
      const vh = window.innerHeight;
      const margin = 8;
      const gap = 4;
      const preferredMax = 288;
      const minBelow = 160;
      const availBelow = vh - r.bottom - gap - margin;
      const availAbove = r.top - gap - margin;
      const flipAbove = availBelow < minBelow && availAbove > availBelow;
      const maxHeight = Math.max(
        120,
        Math.min(preferredMax, flipAbove ? availAbove : availBelow),
      );
      if (flipAbove) {
        setAnchor({ bottom: vh - r.top + gap, left: r.left, width: r.width, maxHeight });
      } else {
        setAnchor({ top: r.bottom + gap, left: r.left, width: r.width, maxHeight });
      }
    };
    place();
    window.addEventListener("scroll", place, true);
    window.addEventListener("resize", place);
    return () => {
      window.removeEventListener("scroll", place, true);
      window.removeEventListener("resize", place);
    };
  }, [open, value]);

  if (disabled) {
    return (
      <div className="h-8 rounded-md border border-dashed border-glass bg-glass px-2 py-1 text-xs text-muted-foreground/60">
        {disabledLabel}
      </div>
    );
  }

  return (
    <div className="relative">
      {value ? (
        <div
          className={cn(
            "flex h-8 items-center justify-between gap-2 rounded-md border bg-glass px-2 text-xs",
            error ? "border-red-400/60" : "border-glass",
          )}
          title={`#${value.number} ${value.subject ?? ""}`}
        >
          <span className="truncate font-mono">
            #{value.number}
            <span className="ml-2 hidden font-sans text-foreground/70 2xl:inline">{value.subject}</span>
          </span>
          <button
            type="button"
            className="text-muted-foreground hover:text-foreground"
            onClick={() => {
              onChange(null);
              setQuery("");
            }}
            aria-label="Clear ticket"
          >
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      ) : (
        <Input
          ref={inputRef}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpen(true);
          }}
          onFocus={() => setOpen(true)}
          onBlur={() => window.setTimeout(() => setOpen(false), 150)}
          placeholder={placeholder}
          className={cn("h-8 text-xs", error && "border-red-400/60")}
          aria-invalid={!!error}
        />
      )}
      {!value && open && anchor &&
        createPortal(
          <div
            className="fixed z-50 overflow-y-auto rounded-md border border-glass bg-[hsl(var(--background))] p-1 shadow-2xl backdrop-blur-xl"
            style={{
              ...(anchor.top !== undefined ? { top: anchor.top } : {}),
              ...(anchor.bottom !== undefined ? { bottom: anchor.bottom } : {}),
              left: anchor.left,
              width: Math.max(anchor.width, 384),
              maxHeight: anchor.maxHeight,
            }}
            onMouseDown={(e) => e.preventDefault()}
          >
            {searching && (
              <div className="px-2 py-1.5 text-xs text-muted-foreground">Searching…</div>
            )}
            {!searching && results.length === 0 && (
              <div className="px-2 py-1.5 text-xs text-muted-foreground">No matches</div>
            )}
            {results.map((r) => (
              <button
                key={r.id}
                type="button"
                onMouseDown={(e) => {
                  e.preventDefault();
                  onChange(r);
                  setQuery("");
                  setOpen(false);
                }}
                className="flex w-full flex-col items-start gap-0 rounded px-2 py-1.5 text-left text-xs hover:bg-glass-hover"
              >
                <span className="font-mono">
                  #{r.number}
                  <span className="ml-2 font-sans text-foreground">{r.subject}</span>
                </span>
                {r.companyName && (
                  <span className="text-[10px] text-muted-foreground">{r.companyName}</span>
                )}
              </button>
            ))}
          </div>,
          document.body,
        )}
      {error && <p className="mt-1 text-[10px] text-red-300">{error}</p>}
    </div>
  );
}
