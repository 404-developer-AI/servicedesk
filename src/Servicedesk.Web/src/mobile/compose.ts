import type { MentionPickerItem } from "@/lib/ticket-api";

/// Mobile composer helpers: plain text in, the same HTML the desktop editor
/// stores out. The server sanitiser (TicketHtmlSanitizer) stays the
/// authority; this only has to produce well-formed, escaped markup.

export type ComposeKind = "note" | "call";

/// A tagged colleague or tagging mailbox. `id` is the picker id — an agent
/// user id, or `mbox:<id>` for a tagging mailbox (same convention as the
/// desktop editor, see splitMentionIds).
export type ComposeMention = { id: string; label: string; kind: "agent" | "mailbox" };

export type ComposeDraft = { text: string; mentions: ComposeMention[] };

const DRAFT_PREFIX = "sd-m-draft:";

function draftKey(kind: ComposeKind, ticketId: string): string {
  return `${DRAFT_PREFIX}${kind}:${ticketId}`;
}

/// Note and Call keep separate draft slots per ticket (composer rule:
/// each kind has its own draft). Local to this device; cleared on sign-out.
export function loadDraft(kind: ComposeKind, ticketId: string): ComposeDraft | null {
  try {
    const raw = window.localStorage.getItem(draftKey(kind, ticketId));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<ComposeDraft>;
    return {
      text: typeof parsed.text === "string" ? parsed.text : "",
      mentions: Array.isArray(parsed.mentions)
        ? parsed.mentions.filter((m): m is ComposeMention => !!m && typeof m.id === "string" && typeof m.label === "string")
        : [],
    };
  } catch {
    return null;
  }
}

export function saveDraft(kind: ComposeKind, ticketId: string, draft: ComposeDraft): void {
  try {
    if (draft.text.trim().length === 0 && draft.mentions.length === 0) {
      window.localStorage.removeItem(draftKey(kind, ticketId));
    } else {
      window.localStorage.setItem(draftKey(kind, ticketId), JSON.stringify(draft));
    }
  } catch {
    // Storage full/blocked: the draft just isn't kept.
  }
}

export function clearDraft(kind: ComposeKind, ticketId: string): void {
  try {
    window.localStorage.removeItem(draftKey(kind, ticketId));
  } catch {
    // ignore
  }
}

/// Sign-out on a possibly shared device: drop every unsent mobile draft.
export function clearAllDrafts(): void {
  try {
    const keys: string[] = [];
    for (let i = 0; i < window.localStorage.length; i++) {
      const k = window.localStorage.key(i);
      if (k?.startsWith(DRAFT_PREFIX)) keys.push(k);
    }
    keys.forEach((k) => window.localStorage.removeItem(k));
  } catch {
    // ignore
  }
}

export function mentionFromPicker(item: MentionPickerItem): ComposeMention {
  // Same labels as the desktop picker: email local-part for agents, the
  // friendly name for a tagging mailbox.
  if (item.kind === "mailbox") return { id: item.id, label: item.label ?? item.email, kind: "mailbox" };
  const at = item.email.indexOf("@");
  return { id: item.id, label: at > 0 ? item.email.slice(0, at) : item.email, kind: "agent" };
}

export function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

// Private-use code point as a placeholder while labels are replaced; it
// cannot come out of escapeHtml, so it never collides with user text.
const PH = "\uE000";
const PLACEHOLDER = /\uE000([^\uE000]+)\uE000/g;

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/// The desktop editor's mention chip (RichTextEditor → Mention.renderHTML).
export function mentionSpan(m: ComposeMention): string {
  const label = escapeHtml(m.label);
  return `<span data-type="mention" class="sd-mention" data-id="${escapeHtml(m.id)}" data-label="${label}">@${label}</span>`;
}

/// Plain text → note HTML. Blank lines split paragraphs, single newlines
/// become <br>. Each `@label` of a tagged person becomes the mention chip;
/// a tagged person whose `@label` is no longer in the text is still tagged
/// (the chips are the source of truth) and is listed in a closing paragraph
/// so the note shows who was notified.
export function composeHtml(text: string, mentions: readonly ComposeMention[]): string {
  const trimmed = text.replace(/\r\n?/g, "\n").trim();
  // Longest label first so "@jan.peeters" wins over "@jan".
  const byLength = [...mentions].sort((a, b) => b.label.length - a.label.length);
  const used = new Set<string>();

  const renderLine = (line: string): string => {
    let html = escapeHtml(line);
    for (const m of byLength) {
      const token = escapeRegExp(`@${escapeHtml(m.label)}`);
      const re = new RegExp(`(^|[^\\w@])${token}(?![\\w-]|\\.\\w)`, "g");
      if (re.test(html)) {
        used.add(m.id);
        html = html.replace(re, (_all, lead: string) => `${lead}${PH}${m.id}${PH}`);
      }
    }
    // Swap placeholders last so a label never matches inside a chip.
    return html.replace(PLACEHOLDER, (_all, id: string) => {
      const m = mentions.find((x) => x.id === id);
      return m ? mentionSpan(m) : "";
    });
  };

  const paragraphs = trimmed.length === 0
    ? []
    : trimmed.split(/\n{2,}/).map((p) => `<p>${p.split("\n").map(renderLine).join("<br>")}</p>`);

  const missing = mentions.filter((m) => !used.has(m.id));
  if (missing.length > 0) paragraphs.push(`<p>${missing.map(mentionSpan).join(" ")}</p>`);
  return paragraphs.join("");
}

/// Agent ids vs tagging-mailbox ids for the request (server buckets).
export function splitMentions(mentions: readonly ComposeMention[]): { userIds: string[]; mailboxIds: string[] } {
  const userIds: string[] = [];
  const mailboxIds: string[] = [];
  for (const m of mentions) {
    if (m.id.startsWith("mbox:")) mailboxIds.push(m.id.slice(5));
    else userIds.push(m.id);
  }
  return { userIds, mailboxIds };
}

/// The `@@query` the caret is in, if any — the trigger that opens the picker
/// while typing (same "@@" trigger as desktop).
export function activeMentionQuery(text: string, caret: number): { start: number; query: string } | null {
  const before = text.slice(0, caret);
  const match = /(^|\s)@@([^\s@]{0,40})$/.exec(before);
  if (!match) return null;
  return { start: before.length - match[2].length - 2, query: match[2] };
}
