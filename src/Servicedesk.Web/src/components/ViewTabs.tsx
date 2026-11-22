import * as React from "react";
import { cn } from "@/lib/utils";

/// v0.1.61 — the one tab bar of the app's work lists (Back office forms,
/// view tabs by category). Approved look: one bottom border under the row;
/// the active tab is marked by a 3 px primary underline on that border, a
/// light primary tint, primary semibold text and a filled primary count
/// badge; inactive tabs are muted with a light primary badge and a hover
/// tint. A count of 0 (or none) shows no badge — a number means work is
/// waiting. Theme tokens only (Steaan + Nebula).

export interface ViewTab {
  id: string;
  label: string;
  icon?: React.ComponentType<{ className?: string }>;
  /** Badge number; hidden when 0 / undefined. */
  count?: number | null;
}

export function ViewTabs({
  tabs,
  value,
  onChange,
  ariaLabel,
  className,
}: {
  tabs: ViewTab[];
  value: string;
  onChange: (id: string) => void;
  ariaLabel: string;
  className?: string;
}) {
  const refs = React.useRef<(HTMLButtonElement | null)[]>([]);
  const onKeyDown = (e: React.KeyboardEvent, index: number) => {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
    e.preventDefault();
    const next = (index + (e.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length;
    refs.current[next]?.focus();
    onChange(tabs[next].id);
  };

  return (
    <div className={cn("sd-view-tabs flex shrink-0 border-b border-border", className)}>
      <div role="tablist" aria-label={ariaLabel} className="flex flex-wrap items-end gap-0.5">
        {tabs.map((t, i) => {
          const active = t.id === value;
          const Icon = t.icon;
          const count = t.count ?? 0;
          return (
            <button
              key={t.id}
              ref={(el) => { refs.current[i] = el; }}
              type="button"
              role="tab"
              aria-selected={active}
              tabIndex={active ? 0 : -1}
              onClick={() => onChange(t.id)}
              onKeyDown={(e) => onKeyDown(e, i)}
              className={cn(
                "sd-view-tab relative -mb-px inline-flex items-center gap-2 rounded-t-md border-b-[3px] px-3.5 pb-2.5 pt-2 text-sm transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring",
                active
                  ? "border-primary bg-primary/8 font-semibold text-primary"
                  : "border-transparent font-medium text-muted-foreground hover:bg-primary/5 hover:text-foreground",
              )}
            >
              {Icon && <Icon className="h-3.5 w-3.5" />}
              <span>{t.label}</span>
              {count > 0 && (
                <span className={cn(
                  "rounded-md px-1.5 text-[11px] font-semibold tabular-nums",
                  active ? "bg-primary text-primary-foreground" : "bg-primary/10 text-primary",
                )}>
                  {count}
                </span>
              )}
            </button>
          );
        })}
      </div>
    </div>
  );
}
