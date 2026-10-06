import * as React from "react";
import { Loader2, Search, X } from "lucide-react";
import type { ViewSearchMode } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

const MODES: { value: ViewSearchMode; label: string; hint: string }[] = [
  { value: "columns", label: "Columns", hint: "Filter instantly on the visible columns" },
  { value: "full", label: "Full", hint: "Also search descriptions, mails and notes" },
];

/// v0.1.18 — the per-view search box: one field with a Columns | Full
/// switch inside it. "/" anywhere on the page focuses it; Esc clears it.
/// Uses the shared glass tokens so it reads flat in Steaan and glassy in
/// Nebula without theme-specific markup.
export function ViewSearchBar({
  value,
  onChange,
  mode,
  onModeChange,
  busy,
  status,
}: {
  value: string;
  onChange: (next: string) => void;
  mode: ViewSearchMode;
  onModeChange: (next: ViewSearchMode) => void;
  busy?: boolean;
  status?: React.ReactNode;
}) {
  const inputRef = React.useRef<HTMLInputElement>(null);

  React.useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key !== "/" || e.ctrlKey || e.metaKey || e.altKey) return;
      const t = e.target as HTMLElement | null;
      if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
      e.preventDefault();
      inputRef.current?.focus();
      inputRef.current?.select();
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);

  const placeholder =
    mode === "full" ? "Search everything in this view…" : "Filter this view by its columns…";

  return (
    <div className="sd-view-search flex min-w-0 flex-col gap-1">
      <div
        className={cn(
          "flex h-9 items-center gap-2 rounded-lg border border-glass bg-glass pl-3 pr-1",
          "transition-colors focus-within:border-primary/50 focus-within:ring-1 focus-within:ring-primary/30",
        )}
      >
        {busy ? (
          <Loader2 className="h-4 w-4 shrink-0 animate-spin text-muted-foreground" />
        ) : (
          <Search className="h-4 w-4 shrink-0 text-muted-foreground" />
        )}
        <input
          ref={inputRef}
          type="search"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Escape") {
              e.preventDefault();
              if (value) onChange("");
              else inputRef.current?.blur();
            }
          }}
          maxLength={200}
          placeholder={placeholder}
          aria-label="Search this view"
          className="min-w-0 flex-1 bg-transparent text-sm text-foreground outline-hidden placeholder:text-muted-foreground [&::-webkit-search-cancel-button]:hidden"
        />
        {value ? (
          <button
            type="button"
            onClick={() => {
              onChange("");
              inputRef.current?.focus();
            }}
            className="flex h-6 w-6 shrink-0 items-center justify-center rounded text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
            aria-label="Clear search"
            title="Clear (Esc)"
          >
            <X className="h-3.5 w-3.5" />
          </button>
        ) : (
          <kbd className="hidden shrink-0 rounded border border-glass px-1.5 py-0.5 text-[10px] text-muted-foreground sm:inline">
            /
          </kbd>
        )}
        <div
          role="radiogroup"
          aria-label="Search mode"
          className="flex shrink-0 items-center rounded-md border border-glass bg-glass-strong p-0.5"
        >
          {MODES.map((m) => {
            const active = m.value === mode;
            return (
              <button
                key={m.value}
                type="button"
                role="radio"
                aria-checked={active}
                title={m.hint}
                onClick={() => {
                  onModeChange(m.value);
                  inputRef.current?.focus();
                }}
                className={cn(
                  "rounded px-2 py-0.5 text-[11px] font-medium transition-colors",
                  active
                    ? "bg-primary/15 text-primary shadow-xs"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {m.label}
              </button>
            );
          })}
        </div>
      </div>
      {status && <div className="px-1 text-[11px] text-muted-foreground">{status}</div>}
    </div>
  );
}
