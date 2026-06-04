import { ordersApi, type AdsolutOrderHeader } from "@/lib/api";
import type { IntakeMentionItem } from "@/components/intake/IntakeMentionList";

/// Display label for an order pill / picker row: the Adsolut document ref
/// ("OR3", or "OR 3" when no kluwer ref) followed by the relation name.
export function orderMentionLabel(o: AdsolutOrderHeader): string {
  const doc = o.kluwerRef ?? `${o.bookCode ?? ""} ${o.docNr ?? ""}`.trim();
  const customer = o.customerName ?? "—";
  return doc ? `${doc} · ${customer}` : customer;
}

/// Fetch Adsolut orders for the "::" picker and map them to mention items
/// (kind: "order"). Honors the admin display status filter server-side. Returns
/// an empty list on any failure so the picker degrades gracefully.
export async function orderMentionItems(query: string): Promise<IntakeMentionItem[]> {
  try {
    const { items } = await ordersApi.pick(query, 8);
    return items.map((o) => ({
      id: o.id,
      name: orderMentionLabel(o),
      description: o.remark,
      kind: "order" as const,
    }));
  } catch {
    return [];
  }
}
