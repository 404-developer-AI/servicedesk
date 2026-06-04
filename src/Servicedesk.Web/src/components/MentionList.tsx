import * as React from "react";
import { Mailbox } from "lucide-react";
import type { MentionPickerItem } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

export type MentionListHandle = {
  onKeyDown: (props: { event: KeyboardEvent }) => boolean;
};

export type MentionListProps = {
  items: MentionPickerItem[];
  loading: boolean;
  command: (props: { id: string; label: string }) => void;
};

/// Typeahead popover for the @@-mention Tiptap extension. Mixes two kinds of
/// targets: agents (display label = email local-part, persisted attr = user-id)
/// and tagging-only mailboxes (display label = friendly name, persisted attr =
/// `mbox:`-prefixed id so the submit-time split routes them to a notification
/// mail). Exposes onKeyDown via ref so the editor can delegate arrow/enter/tab
/// handling without stealing focus from the editor body.
export const MentionList = React.forwardRef<MentionListHandle, MentionListProps>(
  function MentionList({ items, loading, command }, ref) {
    const [selectedIndex, setSelectedIndex] = React.useState(0);

    React.useEffect(() => {
      setSelectedIndex(0);
    }, [items]);

    const labelFor = React.useCallback((item: MentionPickerItem) => {
      if (item.kind === "mailbox") return item.label ?? item.email;
      return item.email.split("@")[0] || item.email;
    }, []);

    const selectItem = React.useCallback(
      (index: number) => {
        const item = items[index];
        if (!item) return;
        command({ id: item.id, label: labelFor(item) });
      },
      [items, command, labelFor],
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
          "rounded-[var(--radius)] border border-glass",
          "bg-popover/95 backdrop-blur-xl shadow-2xl",
          "py-1 text-sm",
        )}
        role="listbox"
      >
        {loading && items.length === 0 ? (
          <div className="px-3 py-2 text-xs text-muted-foreground/70">
            Searching…
          </div>
        ) : items.length === 0 ? (
          <div className="px-3 py-2 text-xs text-muted-foreground/70">
            No matches
          </div>
        ) : (
          items.map((item, i) => {
            const isSelected = i === selectedIndex;
            const isMailbox = item.kind === "mailbox";
            const label = labelFor(item);
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
                    ? "bg-purple-100 text-purple-900 dark:bg-purple-500/15 dark:text-purple-100"
                    : "text-foreground/80 hover:bg-glass-hover",
                )}
              >
                {isMailbox && (
                  <Mailbox className="size-3.5 shrink-0 text-emerald-600 dark:text-emerald-300" />
                )}
                <span className="font-medium shrink-0">
                  {isMailbox ? label : `@${label}`}
                </span>
                <span className="text-xs text-muted-foreground/70 truncate">
                  {item.email}
                </span>
                <span
                  className={cn(
                    "ml-auto shrink-0 rounded-full border px-1.5 py-0.5 text-[9px] font-medium uppercase tracking-wider",
                    isMailbox
                      ? "border-emerald-400/60 bg-emerald-100 text-emerald-800 dark:border-emerald-400/30 dark:bg-emerald-400/10 dark:text-emerald-200"
                      : item.roleName === "Admin"
                        ? "border-amber-400/60 bg-amber-100 text-amber-800 dark:border-amber-400/30 dark:bg-amber-400/10 dark:text-amber-200"
                        : "border-sky-400/60 bg-sky-100 text-sky-800 dark:border-sky-400/30 dark:bg-sky-400/10 dark:text-sky-200",
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
