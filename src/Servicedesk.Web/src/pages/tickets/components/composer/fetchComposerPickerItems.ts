import { ticketFormsApi } from "@/lib/forms-api";
import { composeTemplatesApi } from "@/lib/composeTemplates-api";
import { orderMentionItems } from "@/pages/orders/orderMention";
import { kbLinkMentionItems } from "@/pages/kb/kbLinkMention";
import type { TabType } from "./composerTypes";

/// Items of the `::` picker for the Note / Reply / Call composers.
export async function fetchComposerPickerItems({
  q,
  ticketId,
  queueId,
  statusId,
  tab,
  isCall,
  adsolutOrdersEnabled,
}: {
  q: string;
  ticketId: string;
  queueId?: string | null;
  statusId?: string | null;
  tab: TabType;
  isCall: boolean;
  adsolutOrdersEnabled: boolean | undefined;
}) {
  // Note + reply: only compose-templates surface in the picker. The
  // intake-form chip flow is mail-only (it needs a recipient + send
  // mechanism the internal-note pathway doesn't have).
  // v0.1.17 — only templates scoped for this composer button.
  const list = await composeTemplatesApi.usableCached(
    queueId ?? null,
    statusId ?? null,
    isCall ? "call" : "note",
  );
  const needle = q.trim().toLowerCase();
  const filtered = needle
    ? list.filter(
        (t) =>
          t.name.toLowerCase().includes(needle) ||
          (t.description ?? "").toLowerCase().includes(needle),
      )
    : list;
  const templates = filtered.slice(0, 12).map((t) => ({
    id: t.id,
    name: t.name,
    description: t.description,
    kind: "template" as const,
    bodyHtml: t.bodyHtml,
  }));
  // v0.0.59 — also offer Adsolut orders in the `::` picker for users
  // with the Orders feature flag. Picking one inserts a clickable pill.
  // v0.0.75 — replies also offer Published KB articles as public
  // links (a reply lands in the customer's mailbox, same as mail).
  // Internal notes skip the source: a public link has no audience there.
  // v0.1.52 — interactive forms in the Note and Call composers:
  // picking one opens the fill dialog; saving writes its own internal
  // note. Not offered on Reply (that composer is customer-facing).
  const [orders, kbItems, forms] = await Promise.all([
    adsolutOrdersEnabled ? orderMentionItems(q) : Promise.resolve([]),
    tab === "reply" ? kbLinkMentionItems(q) : Promise.resolve([]),
    tab === "note" || tab === "call"
      ? ticketFormsApi.usableCached(ticketId).catch(() => [])
      : Promise.resolve([]),
  ]);
  const formItems = forms
    .filter((f) => f.inPicker !== false)
    .filter((f) => !needle || f.name.toLowerCase().includes(needle) || f.description.toLowerCase().includes(needle))
    .slice(0, 8)
    .map((f) => ({
      id: f.id,
      name: f.name,
      description: f.description || `${f.fieldCount} field${f.fieldCount === 1 ? "" : "s"}`,
      kind: "form" as const,
      ticketId,
    }));
  return [...formItems, ...templates, ...orders, ...kbItems];
}
