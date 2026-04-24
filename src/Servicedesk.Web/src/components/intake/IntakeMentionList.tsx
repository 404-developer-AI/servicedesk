import * as React from "react";
import { cn } from "@/lib/utils";
import type { IntakeTemplate } from "@/lib/intakeForms-api";

export type IntakeMentionItem = Pick<IntakeTemplate, "id" | "name" | "description">;

export type IntakeMentionListHandle = {
  onKeyDown: (props: { event: KeyboardEvent }) => boolean;
};

export type IntakeMentionListProps = {
  items: IntakeMentionItem[];
  loading: boolean;
  command: (props: { id: string; label: string }) => void;
};

/// Typeahead popover for the `::` intake-form Tiptap extension. Same shape
/// as <see cref="MentionList"/> but keyed to templates instead of agents.
/// The inserted node attrs carry the template id (Guid) and display label;
/// the draft-instance id replaces templateId after the composer resolves
/// the POST /api/tickets/{id}/intake-forms roundtrip.
export const IntakeMentionList = React.forwardRef<IntakeMentionListHandle, IntakeMentionListProps>(
  function IntakeMentionList({ items, loading, command }, ref) {
    const [selectedIndex, setSelectedIndex] = React.useState(0);

    React.useEffect(() => {
      setSelectedIndex(0);
    }, [items]);

    const selectItem = React.useCallback(
      (index: number) => {
        const item = items[index];
        if (!item) return;
        command({ id: item.id, label: item.name });
      },
      [items, command],
    );

    React.useImperativeHandle(ref, () => ({
      onKeyDown: ({ event }) => {
        if (items.length === 0) return false;
        if (event.key === "ArrowDown") {
          setSelectedIndex((i) => (i + 1) % items.length);
          return true;
        }
        if (event.key === "ArrowUp") {
          setSelectedIndex((i) => (i - 1 + items.length) % items.length);
          return true;
        }
        if (event.key === "Enter" || event.key === "Tab") {
          selectItem(selectedIndex);
          return true;
        }
        return false;
      },
    }));

    return (
      <div
        className={cn(
          "min-w-[18rem] max-w-[24rem] max-h-64 overflow-auto",
          "rounded-[var(--radius)] border border-white/10",
          "bg-[hsl(240_10%_6%/0.96)] backdrop-blur-xl shadow-2xl",
          "py-1 text-sm",
        )}
        role="listbox"
      >
        {loading && items.length === 0 ? (
          <div className="px-3 py-2 text-xs text-muted-foreground/70">
            Searching templates…
          </div>
        ) : items.length === 0 ? (
          <div className="px-3 py-2 text-xs text-muted-foreground/70">
            No intake templates match
          </div>
        ) : (
          items.map((item, i) => {
            const isSelected = i === selectedIndex;
            return (
              <button
                key={item.id}
                type="button"
                role="option"
                aria-selected={isSelected}
                onMouseDown={(e) => {
                  e.preventDefault();
                  selectItem(i);
                }}
                onMouseEnter={() => setSelectedIndex(i)}
                className={cn(
                  "w-full flex flex-col items-start gap-0.5 px-3 py-2 text-left transition-colors",
                  isSelected
                    ? "bg-emerald-500/15 text-emerald-100"
                    : "text-foreground/80 hover:bg-white/[0.04]",
                )}
              >
                <span className="font-medium">{item.name}</span>
                {item.description && (
                  <span className="text-xs text-muted-foreground/70 line-clamp-1">
                    {item.description}
                  </span>
                )}
              </button>
            );
          })
        )}
      </div>
    );
  },
);
