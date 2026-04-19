import * as React from "react";
import type { AgentUser } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

export type MentionListHandle = {
  onKeyDown: (props: { event: KeyboardEvent }) => boolean;
};

export type MentionListProps = {
  items: AgentUser[];
  loading: boolean;
  command: (props: { id: string; label: string }) => void;
};

/// Agent-typeahead popover for the @@-mention Tiptap extension.
/// Exposes onKeyDown via ref so the editor can delegate arrow/enter/tab
/// handling into the popover without stealing focus from the editor body.
/// Display label is the email local-part (`john@example.com` → `@john`);
/// the persisted attr is the agent's user-id (Guid).
export const MentionList = React.forwardRef<MentionListHandle, MentionListProps>(
  function MentionList({ items, loading, command }, ref) {
    const [selectedIndex, setSelectedIndex] = React.useState(0);

    React.useEffect(() => {
      setSelectedIndex(0);
    }, [items]);

    const selectItem = React.useCallback(
      (index: number) => {
        const item = items[index];
        if (!item) return;
        const localPart = item.email.split("@")[0] || item.email;
        command({ id: item.id, label: localPart });
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
          "min-w-[16rem] max-w-[22rem] max-h-64 overflow-auto",
          "rounded-[var(--radius)] border border-white/10",
          "bg-[hsl(240_10%_6%/0.96)] backdrop-blur-xl shadow-2xl",
          "py-1 text-sm",
        )}
        role="listbox"
      >
        {loading && items.length === 0 ? (
          <div className="px-3 py-2 text-xs text-muted-foreground/70">
            Searching agents…
          </div>
        ) : items.length === 0 ? (
          <div className="px-3 py-2 text-xs text-muted-foreground/70">
            No agents match
          </div>
        ) : (
          items.map((item, i) => {
            const localPart = item.email.split("@")[0] || item.email;
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
                  "w-full flex items-center gap-2 px-3 py-1.5 text-left transition-colors",
                  isSelected
                    ? "bg-purple-500/15 text-purple-100"
                    : "text-foreground/80 hover:bg-white/[0.04]",
                )}
              >
                <span className="font-medium shrink-0">@{localPart}</span>
                <span className="text-xs text-muted-foreground/70 truncate">
                  {item.email}
                </span>
                <span
                  className={cn(
                    "ml-auto text-[10px] uppercase tracking-wide shrink-0 opacity-60",
                    item.roleName === "Admin" ? "text-amber-300" : "text-sky-300",
                  )}
                >
                  {item.roleName}
                </span>
              </button>
            );
          })
        )}
      </div>
    );
  },
);
