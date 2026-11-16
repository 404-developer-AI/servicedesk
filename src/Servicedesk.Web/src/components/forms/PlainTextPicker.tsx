import * as React from "react";
import { Popover, PopoverAnchor, PopoverContent } from "@/components/ui/popover";
import { IntakeMentionList, type IntakeMentionItem, type IntakeMentionListHandle } from "@/components/intake/IntakeMentionList";
import { debounceAsync } from "@/lib/asyncDebounce";
import { applyPickerInsert, findPickerTrigger } from "./formFieldPicker";

/// v0.1.55 — the `::` lookup for a plain `<input>` (form short text, the
/// template editor's default value). Same keystroke and the same list as the
/// rich-text editor's `::` picker; a pick replaces the typed `::query` with
/// plain text.

const PICKER_DEBOUNCE_MS = 250;

type Props = Omit<React.InputHTMLAttributes<HTMLInputElement>, "value" | "onChange"> & {
  value: string;
  /// `picked` is set when the change comes from a `::` pick: the item and
  /// the text it became (trimmed to the field limit).
  onValueChange: (v: string, picked?: { item: IntakeMentionItem; text: string }) => void;
  query: (q: string) => Promise<IntakeMentionItem[]>;
  /// Plain text a picked item becomes.
  toText: (item: IntakeMentionItem) => string;
};

export const PlainTextPicker = React.forwardRef<HTMLInputElement, Props>(function PlainTextPicker(
  { value, onValueChange, query, toText, onKeyDown, maxLength, ...inputProps },
  forwardedRef,
) {
  const inputRef = React.useRef<HTMLInputElement | null>(null);
  const listRef = React.useRef<IntakeMentionListHandle>(null);
  const [trigger, setTrigger] = React.useState<{ start: number; query: string } | null>(null);
  const [items, setItems] = React.useState<IntakeMentionItem[]>([]);
  const [loading, setLoading] = React.useState(false);

  // `query` is memoised by the caller (it changes only when the ticket's
  // variables arrive), so the debounced lookup is rebuilt rarely.
  const lookup = React.useMemo(
    () => debounceAsync(async (q: string) => {
      try { return await query(q); } catch { return [] as IntakeMentionItem[]; }
    }, PICKER_DEBOUNCE_MS),
    [query],
  );

  const activeQuery = trigger?.query ?? null;
  React.useEffect(() => {
    if (activeQuery === null) return;
    let cancelled = false;
    setLoading(true);
    void lookup(activeQuery).then((r) => {
      if (cancelled) return;
      setItems(r ?? []);
      setLoading(false);
    });
    return () => { cancelled = true; };
  }, [activeQuery, lookup]);

  const sync = (el: HTMLInputElement) => {
    const caret = el.selectionStart ?? el.value.length;
    setTrigger(el.selectionStart === el.selectionEnd ? findPickerTrigger(el.value, caret) : null);
  };

  const pick = (item: IntakeMentionItem) => {
    const el = inputRef.current;
    if (!el || !trigger) return;
    const caret = el.selectionStart ?? value.length;
    let insert = toText(item);
    if (maxLength != null) {
      // Never push the field over its limit: trim the insert to what fits.
      const room = maxLength - (value.length - (caret - trigger.start));
      insert = insert.slice(0, Math.max(0, room));
    }
    const next = applyPickerInsert(value, trigger.start, caret, insert);
    onValueChange(next.text, { item, text: insert });
    setTrigger(null);
    setItems([]);
    requestAnimationFrame(() => {
      el.focus();
      el.setSelectionRange(next.caret, next.caret);
    });
  };

  const open = trigger !== null;

  return (
    <Popover open={open} onOpenChange={(o) => { if (!o) setTrigger(null); }}>
      <PopoverAnchor asChild>
        <input
          {...inputProps}
          ref={(el) => {
            inputRef.current = el;
            if (typeof forwardedRef === "function") forwardedRef(el);
            else if (forwardedRef) forwardedRef.current = el;
          }}
          maxLength={maxLength}
          value={value}
          onChange={(e) => {
            onValueChange(e.target.value);
            sync(e.target);
          }}
          onSelect={(e) => sync(e.currentTarget)}
          onBlur={() => setTrigger(null)}
          onKeyDown={(e) => {
            if (open && ["ArrowDown", "ArrowUp", "Enter", "Tab"].includes(e.key)) {
              // The list handles navigation and picking; keep Enter from
              // reaching the dialog's Ctrl+Enter save.
              if (listRef.current?.onKeyDown({ event: e.nativeEvent })) {
                e.preventDefault();
                e.stopPropagation();
                return;
              }
            }
            onKeyDown?.(e);
          }}
        />
      </PopoverAnchor>
      <PopoverContent
        className="w-auto border-0 bg-transparent p-0 shadow-none"
        onOpenAutoFocus={(e) => e.preventDefault()}
        onCloseAutoFocus={(e) => e.preventDefault()}
        onInteractOutside={(e) => { if (e.target === inputRef.current) e.preventDefault(); }}
      >
        <IntakeMentionList ref={listRef} items={items} loading={loading} command={pick} />
      </PopoverContent>
    </Popover>
  );
});
