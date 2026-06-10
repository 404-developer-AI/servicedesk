import * as React from "react";
import { BookOpen, ClipboardList, FileText, ShoppingCart } from "lucide-react";
import { cn } from "@/lib/utils";

/// Items shown in the `::` typeahead. The picker now mixes intake-form
/// templates and pre-canned compose templates — both surface through the
/// same trigger so an agent only has to learn one keystroke.
///
/// `kind` determines what happens on select (handled by the Tiptap
/// suggestion plugin in <RichTextEditor>):
///   - "intake"   → insert an intake-form chip + async draft-instance create
///   - "template" → insert the template's HTML body inline (no chip)
///   - "order"    → insert a clickable order pill
///   - "kb"       → insert a hyperlink to the article's public reader page
export type IntakeMentionItem = {
  id: string;
  name: string;
  description?: string | null;
  kind: "intake" | "template" | "order" | "kb";
  /// Only set for kind = "template". The picker hands it through to the
  /// suggestion command so the editor can insertContent at the trigger range.
  bodyHtml?: string;
  /// Only set for kind = "kb": the absolute public URL the inserted link
  /// points at (built from the install's public base URL).
  href?: string;
};

export type IntakeMentionListHandle = {
  onKeyDown: (props: { event: KeyboardEvent }) => boolean;
};

export type IntakeMentionListProps = {
  items: IntakeMentionItem[];
  loading: boolean;
  /// The picker forwards the full item so the Tiptap suggestion plugin can
  /// branch on `kind` and skip the chip-flow for templates.
  command: (props: IntakeMentionItem) => void;
};

/// Typeahead popover for the `::` Tiptap extension. Shared by intake-form
/// templates and compose templates; the row badge tells them apart.
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
        command(item);
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
          "min-w-[20rem] max-w-[28rem] max-h-72 overflow-auto",
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
            const Icon =
              item.kind === "template" ? FileText
                : item.kind === "order" ? ShoppingCart
                : item.kind === "kb" ? BookOpen
                : ClipboardList;
            const kindLabel =
              item.kind === "template" ? "Template"
                : item.kind === "order" ? "Order"
                : item.kind === "kb" ? "KB article"
                : "Intake";
            return (
              <button
                key={`${item.kind}:${item.id}`}
                type="button"
                role="option"
                aria-selected={isSelected}
                onMouseDown={(e) => {
                  e.preventDefault();
                  selectItem(i);
                }}
                onMouseEnter={() => setSelectedIndex(i)}
                className={cn(
                  "w-full flex items-start gap-3 px-3 py-2 text-left transition-colors",
                  isSelected
                    ? "bg-emerald-100 text-emerald-900 dark:bg-emerald-500/15 dark:text-emerald-100"
                    : "text-foreground/80 hover:bg-glass-hover",
                )}
              >
                <Icon
                  className={cn(
                    "mt-0.5 h-4 w-4 shrink-0",
                    item.kind === "template"
                      ? "text-violet-700 dark:text-violet-300"
                      : item.kind === "kb"
                        ? "text-emerald-700 dark:text-emerald-300"
                        : "text-cyan-700 dark:text-cyan-300",
                  )}
                />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="truncate font-medium">{item.name}</span>
                    <span
                      className={cn(
                        "shrink-0 rounded-full border px-1.5 py-0.5 text-[9px] font-medium uppercase tracking-wider",
                        item.kind === "template"
                          ? "border-violet-400/60 bg-violet-100 text-violet-800 dark:border-violet-400/30 dark:bg-violet-400/10 dark:text-violet-200"
                          : item.kind === "kb"
                            ? "border-emerald-400/60 bg-emerald-100 text-emerald-800 dark:border-emerald-400/30 dark:bg-emerald-400/10 dark:text-emerald-200"
                            : "border-cyan-400/60 bg-cyan-100 text-cyan-800 dark:border-cyan-400/30 dark:bg-cyan-400/10 dark:text-cyan-200",
                      )}
                    >
                      {kindLabel}
                    </span>
                  </div>
                  {item.description && (
                    <span className="block truncate text-xs text-muted-foreground/70">
                      {item.description}
                    </span>
                  )}
                </div>
              </button>
            );
          })
        )}
      </div>
    );
  },
);
