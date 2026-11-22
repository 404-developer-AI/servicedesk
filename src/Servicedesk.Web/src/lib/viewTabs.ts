import type { TicketListItem } from "@/lib/ticket-api";

/// v0.1.61 — view tabs by category. A view lists the categories that get
/// their own tab (`DisplayConfig.tabCategoryIds`, admin-ordered); every other
/// ticket of the view sits in "Other" — the default tab, where agents work as
/// before. Tabs only filter the rows the view already loaded (single-load
/// model): no extra query, the server's view filters and queue access stay
/// the source of truth.
///
/// A tab's badge counts only work that is ready: tickets whose status is not
/// in a waiting category. Pending tickets never count, except once their
/// pending-till has passed (normally a trigger reopens them first). Resolved
/// and Closed tickets never count.

export const OTHER_TAB = "other";

type TabItem = Pick<TicketListItem, "categoryId" | "statusStateCategory" | "pendingTillUtc">;

export function tabOf(item: Pick<TicketListItem, "categoryId">, tabCategoryIds: readonly string[]): string {
  return item.categoryId && tabCategoryIds.includes(item.categoryId) ? item.categoryId : OTHER_TAB;
}

export function isReadyWork(item: TabItem, nowMs: number): boolean {
  const cat = item.statusStateCategory;
  if (cat === "New" || cat === "Open") return true;
  if (cat === "Pending") return !!item.pendingTillUtc && Date.parse(item.pendingTillUtc) <= nowMs;
  return false;
}

export function countReadyByTab(
  items: readonly TabItem[], tabCategoryIds: readonly string[], nowMs: number,
): Record<string, number> {
  const counts: Record<string, number> = { [OTHER_TAB]: 0 };
  for (const id of tabCategoryIds) counts[id] = 0;
  for (const t of items) {
    if (!isReadyWork(t, nowMs)) continue;
    const k = tabOf(t, tabCategoryIds);
    counts[k] = (counts[k] ?? 0) + 1;
  }
  return counts;
}

export function filterByTab<T extends Pick<TicketListItem, "categoryId">>(
  items: readonly T[], tab: string, tabCategoryIds: readonly string[],
): T[] {
  return items.filter((t) => tabOf(t, tabCategoryIds) === tab);
}
