import * as React from "react";
import type { TicketListItem } from "@/lib/ticket-api";
import type { TicketSelection } from "./GroupedTicketList";

/// v0.0.102 — row selection on the ticket list (moved out of TicketListPage
/// in v0.1.67). Selection is offered when bulk actions are on or the user has
/// Campaigns; it is always "loaded rows only". A different view/filter
/// (filters / view id) starts clean, and ids that left the loaded list are pruned so
/// the count and the dialogs never include tickets the agent can no longer see.
export function useTicketBulkSelection(
  items: TicketListItem[], filters: unknown, viewId: string | null | undefined, enabled: boolean,
) {
  const [selected, setSelected] = React.useState<Set<string>>(() => new Set());

  React.useEffect(() => {
    setSelected(new Set());
  }, [filters, viewId]);

  React.useEffect(() => {
    if (selected.size === 0) return;
    const present = new Set(items.map((t) => t.id));
    let changed = false;
    for (const id of selected) if (!present.has(id)) { changed = true; break; }
    if (!changed) return;
    setSelected((cur) => new Set([...cur].filter((id) => present.has(id))));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items]);

  const selection: TicketSelection | undefined = React.useMemo(
    () =>
      enabled
        ? {
            selected,
            onToggle: (id) =>
              setSelected((cur) => {
                const next = new Set(cur);
                if (next.has(id)) next.delete(id);
                else next.add(id);
                return next;
              }),
            onSetMany: (ids, checked) =>
              setSelected((cur) => {
                const next = new Set(cur);
                for (const id of ids) {
                  if (checked) next.add(id);
                  else next.delete(id);
                }
                return next;
              }),
          }
        : undefined,
    [enabled, selected],
  );
  const selectedItems = React.useMemo(
    () => (selected.size === 0 ? [] : items.filter((t) => selected.has(t.id))),
    [items, selected],
  );
  const clear = React.useCallback(() => setSelected(new Set()), []);
  return { selection, selectedItems, clear };
}
