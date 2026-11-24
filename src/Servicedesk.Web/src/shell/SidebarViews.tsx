import * as React from "react";
import { ChevronRight, Eye, Pin, PinOff } from "lucide-react";
import { cn } from "@/lib/utils";
import type { View } from "@/lib/views-api";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";

/// Saved views in the sidebar: inline rows for pinned views and the flyout
/// holding the rest. Split out of Sidebar.tsx (file-size budget).
/// v0.0.65 — a single saved-view the user pinned inline. Mirrors NavRow: the
/// row navigates to the view; when expanded a pin-off button appears on hover
/// to return it to the flyout. All views share the Eye glyph, so when collapsed
/// the row leans on its title tooltip to stay identifiable.
export function ViewRow({
  view,
  active,
  collapsed,
  onSelect,
  onUnpin,
}: {
  view: View;
  active: boolean;
  collapsed: boolean;
  onSelect: () => void;
  onUnpin: () => void;
}) {
  const button = (
    <button
      type="button"
      onClick={onSelect}
      title={collapsed ? view.name : undefined}
      className={cn(
        "sd-nav-row flex w-full items-center gap-2 rounded-lg px-3 py-1.5 text-sm transition-colors",
        active
          ? "sd-nav-row-active bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
          : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
        collapsed ? "justify-center px-2" : "pr-9",
      )}
    >
      <Eye className={cn("h-3.5 w-3.5 shrink-0", active && "text-primary")} />
      {!collapsed && <span className="truncate">{view.name}</span>}
    </button>
  );

  if (collapsed) return button;

  return (
    <div className="group/viewrow relative">
      {button}
      <button
        type="button"
        title={`Unpin ${view.name}`}
        aria-label={`Unpin ${view.name}`}
        onClick={onUnpin}
        className="absolute right-1.5 top-1/2 flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-md text-muted-foreground/70 opacity-0 transition-all hover:bg-glass-strong hover:text-foreground focus-visible:opacity-100 group-hover/viewrow:opacity-100"
      >
        <PinOff className="h-3.5 w-3.5" />
      </button>
    </div>
  );
}

/// v0.0.65 — single "Views" entry that opens a flyout to the right listing
/// every saved view the user has not pinned inline. Mirrors FeaturesFlyout:
/// each row navigates; the trailing pin button lifts the view out into the rail
/// (persisted per-user). The list scrolls past ~6 entries so the flyout never
/// grows unbounded. The trigger reads as active whenever a contained view is
/// the one currently open.
export function ViewsFlyout({
  views,
  collapsed,
  activeViewId,
  onSelect,
  onPin,
}: {
  views: readonly View[];
  collapsed: boolean;
  activeViewId: string | null;
  onSelect: (id: string) => void;
  onPin: (id: string) => void;
}) {
  const [open, setOpen] = React.useState(false);
  const anyActive = views.some((v) => v.id === activeViewId);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          title="Views"
          className={cn(
            "group flex w-full items-center gap-2 rounded-lg px-3 py-1.5 text-sm transition-colors",
            anyActive
              ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
              : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
            collapsed && "justify-center px-2",
          )}
        >
          <Eye className={cn("h-3.5 w-3.5 shrink-0", anyActive && "text-primary")} />
          {!collapsed && (
            <>
              <span className="truncate">Views</span>
              <ChevronRight className="ml-auto h-3.5 w-3.5 opacity-60 transition-transform group-hover:translate-x-0.5" />
            </>
          )}
        </button>
      </PopoverTrigger>
      <PopoverContent side="right" align="start" sideOffset={12} className="w-60 p-1.5">
        <div className="flex items-center justify-between px-2 pb-1 pt-0.5">
          <span className="text-[10px] font-medium uppercase tracking-widest text-muted-foreground/60">
            Views
          </span>
          <span className="text-[10px] text-muted-foreground/50">Pin to sidebar</span>
        </div>
        <div className="max-h-48 space-y-0.5 overflow-y-auto">
          {views.map((view) => {
            const active = view.id === activeViewId;
            return (
              <div
                key={view.id}
                className="group/view flex items-center gap-1 rounded-md hover:bg-glass-hover"
              >
                <button
                  type="button"
                  onClick={() => {
                    onSelect(view.id);
                    setOpen(false);
                  }}
                  className={cn(
                    "flex min-w-0 flex-1 items-center gap-2 rounded-md px-2 py-1.5 text-sm transition-colors",
                    active ? "text-foreground" : "text-muted-foreground hover:text-foreground",
                  )}
                >
                  <Eye className={cn("h-4 w-4 shrink-0", active && "text-primary")} />
                  <span className="truncate">{view.name}</span>
                </button>
                <button
                  type="button"
                  title={`Pin ${view.name} to sidebar`}
                  aria-label={`Pin ${view.name} to sidebar`}
                  onClick={() => onPin(view.id)}
                  className="mr-1 flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-muted-foreground/60 opacity-0 transition-all hover:bg-glass-strong hover:text-foreground focus-visible:opacity-100 group-hover/view:opacity-100"
                >
                  <Pin className="h-3.5 w-3.5" />
                </button>
              </div>
            );
          })}
        </div>
      </PopoverContent>
    </Popover>
  );
}
