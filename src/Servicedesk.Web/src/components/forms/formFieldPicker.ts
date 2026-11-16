import type { IntakeMentionItem } from "@/components/intake/IntakeMentionList";
import { kbApi } from "@/lib/kb-api";
import { orderMentionItems } from "@/pages/orders/orderMention";

/// v0.1.55 — the `::` lookup inside form text fields (short and long text).
/// Offers the ticket's variables (contact, company, ticket — the compose
/// `{{token}}` set, already resolved for this ticket), Adsolut orders and
/// KB articles. No templates and no forms: a form never nests a form.
/// Every source is enforced server-side (orders: Adsolut Orders flag; KB:
/// per-user KB access; tokens: queue access on the ticket); the client
/// checks here only spare a request that would come back 403.

export type FormFieldPickerContext = {
  /// Resolved `{{token}}` → value map for the form's ticket.
  tokens: Record<string, string> | undefined;
  ordersEnabled: boolean;
  kbEnabled: boolean;
};

const MAX_VARIABLES = 12;
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

/// "{{contact.firstName}}" → "Contact · First name".
export function tokenLabel(token: string): string {
  const name = token.replace(/^\{\{\s*|\s*\}\}$/g, "");
  const [group, ...rest] = name.split(".");
  const field = rest.join(".").replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
  const cap = (s: string) => (s ? s[0].toUpperCase() + s.slice(1) : s);
  return field ? `${cap(group)} · ${cap(field)}` : cap(group);
}

/// Variables with a value for this ticket, filtered by the typed query.
export function variableItems(tokens: Record<string, string> | undefined, query: string): IntakeMentionItem[] {
  const needle = query.trim().toLowerCase();
  return Object.entries(tokens ?? {})
    .filter(([, value]) => value.trim() !== "")
    .map(([token, value]) => ({ token, value, label: tokenLabel(token) }))
    .filter((v) => !needle || v.label.toLowerCase().includes(needle) || v.value.toLowerCase().includes(needle))
    .slice(0, MAX_VARIABLES)
    .map((v) => ({
      id: v.token,
      name: v.label,
      description: v.value,
      kind: "variable" as const,
      text: v.value,
    }));
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
  return [...variableItems(ctx.tokens, query), ...orders, ...kb];
}

/// What a picked item becomes in a plain-text (short text) field.
export function pickerItemAsText(item: IntakeMentionItem): string {
  switch (item.kind) {
    case "variable":
      return item.text ?? "";
    case "kb":
      return item.href ? `${item.name} (${item.href})` : item.name;
    default:
      return item.name;
  }
}
