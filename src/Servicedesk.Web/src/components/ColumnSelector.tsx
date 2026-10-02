import * as React from "react";
import { Columns3, Lock, RotateCcw } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useColumnPrefsStore, DEFAULT_COLUMNS } from "@/stores/useColumnPrefsStore";
import { SortableColumnList } from "@/components/SortableColumnList";
import { cn } from "@/lib/utils";

/// Agent column picker for the ticket list. v0.1.18: ordered (drag to
/// reorder) and hidden entirely when the active view locks its columns —
/// a quiet hint takes its place so the agent knows why.
export function ColumnSelector() {
  const { visibleColumns, setVisibleColumns, resetToDefaults, locked, loaded, activeViewId } = useColumnPrefsStore();
  const [open, setOpen] = React.useState(false);

  if (locked) {
    return (
      <span
        className="sd-columns-locked inline-flex h-8 items-center gap-1.5 px-1 text-xs text-muted-foreground/80"
        title="The columns and their order are set by this view."
      >
        <Lock className="h-3 w-3" />
        Columns are set by this view
      </span>
    );
  }
  if (!loaded) return null;

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className={cn(
            "flex items-center gap-1.5 h-8 px-3 rounded-md border border-glass",
            "bg-glass text-sm text-muted-foreground",
            "hover:bg-glass-hover hover:text-foreground transition-colors",
          )}
        >
          <Columns3 className="h-3.5 w-3.5" />
          Columns
        </button>
      </PopoverTrigger>

      <PopoverContent className="w-60 p-2" align="end">
        <div className="px-1 pb-2 text-[11px] uppercase tracking-wider text-muted-foreground/70">
          Columns — drag to reorder
        </div>
        <SortableColumnList value={visibleColumns} onChange={setVisibleColumns} />

        <div className="mt-2 border-t border-glass pt-2">
          <button
            type="button"
            onClick={() => {
              resetToDefaults();
              setOpen(false);
            }}
            className="flex w-full items-center gap-1.5 rounded px-2 py-1.5 text-xs text-muted-foreground hover:text-foreground hover:bg-glass-hover transition-colors"
          >
            <RotateCcw className="h-3 w-3" />
            {activeViewId ? "Reset to the view's columns" : "Reset to defaults"}
          </button>
        </div>
      </PopoverContent>
    </Popover>
  );
}

export { DEFAULT_COLUMNS };
