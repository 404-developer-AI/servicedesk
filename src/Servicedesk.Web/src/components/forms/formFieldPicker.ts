import type { IntakeMentionItem } from "@/components/intake/IntakeMentionList";
import { kbApi } from "@/lib/kb-api";
import { orderMentionItems } from "@/pages/orders/orderMention";

/// v0.1.55 — the `::` lookup inside form text fields (short and long text).
/// Offers Adsolut orders and KB articles. No templates and no forms (a form
/// never nests a form), and since v0.1.58 no ticket variables either — they
/// cluttered the list; a template default can still hold `{{tokens}}`.
/// Every source is enforced server-side (orders: Adsolut Orders flag; KB:
/// per-user KB access); the client checks here only spare a request that
/// would come back 403.

export type FormFieldPickerContext = {
  ordersEnabled: boolean;
  kbEnabled: boolean;
};

const MAX_QUERY = 40;

/// The `::query` that ends at the caret in a plain input, or null. The query
/// has no spaces, like the editor's suggestion trigger.
export function findPickerTrigger(text: string, caret: number): { start: number; query: string } | null {
  const before = text.slice(0, caret);
  const m = new RegExp(`::([^\\s:]{0,${MAX_QUERY}})$`).exec(before);
  return m ? { start: m.index, query: m[1] } : null;
}

/// Replace `text[start, caret)` with the insert; returns the new text and caret.
export function applyPickerInsert(text: string, start: number, caret: number, insert: string): { text: string; caret: number } {
  const next = text.slice(0, start) + insert + text.slice(caret);
  return { text: next, caret: start + insert.length };
}

/// Published KB articles as links to the agent-facing article page (a form
/// is internal, so the public reader link would be the wrong audience).
async function kbItems(query: string): Promise<IntakeMentionItem[]> {
  try {
    const { items } = await kbApi.listArticles({
      status: "Published",
      search: query.trim() || undefined,
      pageSize: 6,
    });
    return items.map((a) => ({
      id: a.id,
      name: a.title,
      description: "Knowledge base article",
      kind: "kb" as const,
      href: `${window.location.origin}/kb/articles/${a.id}`,
    }));
  } catch {
    return [];
  }
}

export async function formFieldPickerItems(ctx: FormFieldPickerContext, query: string): Promise<IntakeMentionItem[]> {
  const [orders, kb] = await Promise.all([
    ctx.ordersEnabled ? orderMentionItems(query) : Promise.resolve([]),
    ctx.kbEnabled ? kbItems(query) : Promise.resolve([]),
  ]);
  return [...orders, ...kb];
}

/// What a picked item becomes in a plain-text (short text) field.
export function pickerItemAsText(item: IntakeMentionItem): string {
  switch (item.kind) {
    case "kb":
      return item.href ? `${item.name} (${item.href})` : item.name;
    default:
      return item.name;
  }
}
