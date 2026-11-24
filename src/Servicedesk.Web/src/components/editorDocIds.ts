/// Walk a Tiptap JSON doc and collect the `attrs.instanceId` of every
/// `intakeMention` node, in document order, deduplicated. Only nodes whose
/// draft-instance was successfully created (i.e. attrs.instanceId is a
/// string) are included — freshly-inserted chips waiting on the POST still
/// have `instanceId: null` and must not leak into LinkedFormIds.
export function extractIntakeInstanceIds(doc: unknown): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  const walk = (node: unknown) => {
    if (!node || typeof node !== "object") return;
    const n = node as {
      type?: string;
      attrs?: { instanceId?: string | null };
      content?: unknown[];
    };
    if (n.type === "intakeMention" && typeof n.attrs?.instanceId === "string") {
      const id = n.attrs.instanceId;
      if (!seen.has(id)) {
        seen.add(id);
        out.push(id);
      }
    }
    if (Array.isArray(n.content)) {
      for (const child of n.content) walk(child);
    }
  };
  walk(doc);
  return out;
}

/// Walk a Tiptap JSON doc and collect the `attrs.id` of every `mention`
/// node, in document order, deduplicated. Used at submit-time by the forms
/// to pass a concise `mentionedUserIds[]` to the server without re-parsing
/// the HTML string.
export function extractMentionIds(doc: unknown): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  const walk = (node: unknown) => {
    if (!node || typeof node !== "object") return;
    const n = node as { type?: string; attrs?: { id?: string }; content?: unknown[] };
    if (n.type === "mention" && typeof n.attrs?.id === "string") {
      const id = n.attrs.id;
      if (!seen.has(id)) {
        seen.add(id);
        out.push(id);
      }
    }
    if (Array.isArray(n.content)) {
      for (const child of n.content) walk(child);
    }
  };
  walk(doc);
  return out;
}

/// Split the flat mention-id list emitted by `onMentionsChange` into the
/// server-side buckets. Tagging-mailbox ids are carried in the document with a
/// `mbox:` prefix and group-tag ids (v0.1.54) with `group:` (so the mention
/// node needs no schema change); we strip the prefix here and route them.
/// Everything else is an agent user-id.
export function splitMentionIds(ids: string[]): {
  userIds: string[];
  mailboxIds: string[];
  groupIds: string[];
} {
  const userIds: string[] = [];
  const mailboxIds: string[] = [];
  const groupIds: string[] = [];
  for (const id of ids) {
    if (id.startsWith("mbox:")) mailboxIds.push(id.slice(5));
    else if (id.startsWith("group:")) groupIds.push(id.slice(6));
    else userIds.push(id);
  }
  return { userIds, mailboxIds, groupIds };
}
