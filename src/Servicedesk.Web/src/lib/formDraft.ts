import { z } from "zod";
import { formatDate, formatDateTime, formatTime } from "@/lib/dateFormat";
import type {
  AgentAnswer,
  AttachmentAnswer,
  InviteeAnswer,
  ArticleRowAnswer,
  FormAnswers,
  FormAnswersInput,
  FormField,
  FormOrderRef,
  RefAnswer,
  ShortTextOrdersAnswer,
} from "@/lib/forms-api";
import { escapeHtml } from "@/lib/format";
import { htmlToText } from "@/lib/sanitize";
import { substituteFormDefault } from "@/lib/composeTokens";

/// v0.1.52 — the in-dialog editing model for a ticket form, plus conversion
/// to the API payload and back from stored answers. Validation here is fast
/// feedback only; the server re-validates every answer against the form's
/// snapshot and is the authority.

export type ArticleDraftRow = { key: string; articleId: string; code: string; name: string; quantity: string };

export type DraftValue =
  /// `orders`: Adsolut orders picked through `::` in a short text field.
  | { kind: "text"; value: string; orders?: FormOrderRef[] }
  /// Rich-text LongText (v0.1.55). `legacy` keeps a stored plain-text answer
  /// and the HTML it was converted to, so an untouched legacy field is sent
  /// back unchanged instead of reading as an edit.
  | { kind: "html"; value: string; legacy?: { text: string; html: string } }
  | { kind: "bool"; value: boolean }
  | { kind: "options"; value: string[] }
  | { kind: "ref"; value: RefAnswer | null }
  | { kind: "datetime"; value: string | null }
  | { kind: "articles"; value: ArticleDraftRow[] }
  /// v0.1.61 — Agent with "multiple": picked agents in order, one may be organizer.
  | { kind: "agents"; value: AgentAnswer[] }
  /// v0.1.61 — people to invite: contacts and/or free e-mail addresses.
  | { kind: "invitees"; value: InviteeAnswer[] }
  /// v0.1.61 — files uploaded to the ticket for this form.
  | { kind: "files"; value: AttachmentAnswer[] };

/// v0.1.61 — "show when": a field is shown (and validated, and sent) only
/// while its controlling checkbox is ticked — or, with showWhenUnchecked,
/// not ticked (never answered counts as not ticked). Same rule as the server.
export function isFieldShown(f: FormField, draft: FormDraft): boolean {
  if (!f.showWhen) return true;
  const v = draft[f.showWhen];
  const ticked = v?.kind === "bool" && v.value === true;
  return f.showWhenUnchecked ? !ticked : ticked;
}

/// v0.1.61 — end of a Duration that counts from a DateTime field (start +
/// duration), as ISO; null when incomplete. Same rule as the server.
function addMinutesIso(startIso: string | null | undefined, minutes: number | null): string | null {
  if (!startIso || !minutes || minutes <= 0) return null;
  const ms = Date.parse(startIso);
  return Number.isNaN(ms) ? null : new Date(ms + minutes * 60_000).toISOString();
}

export function durationEndIso(f: FormField, draft: FormDraft): string | null {
  if (f.type !== "Duration" || !f.durationFrom) return null;
  const start = draft[f.durationFrom];
  const dur = draft[f.id];
  if (start?.kind !== "datetime" || dur?.kind !== "text") return null;
  return addMinutesIso(start.value, parseDuration(dur.value));
}

/// The same end from stored answers (the read-only form view).
export function durationEndIsoFromAnswers(f: FormField, answers: Record<string, unknown>): string | null {
  if (f.type !== "Duration" || !f.durationFrom) return null;
  const start = answers[f.durationFrom];
  const minutes = answers[f.id];
  return addMinutesIso(typeof start === "string" ? start : null, typeof minutes === "number" ? minutes : null);
}

/// "until 14:30", or "until 23/11/2026 - 01:00" when it ends on another day
/// (server zone).
export function durationEndLabel(endIso: string, startIso: string | null | undefined, zone: string | null): string {
  const sameDay = !!startIso && formatDate(startIso, zone) === formatDate(endIso, zone);
  return `until ${sameDay ? formatTime(endIso, zone) : formatDateTime(endIso, zone)}`;
}

/// A pragmatic e-mail check (one @, a dot in the domain, no spaces) — the
/// same rule the server applies to invitees.
export function isEmail(s: string): boolean {
  const v = s.trim();
  if (v.length < 3 || v.length > 320 || /\s/.test(v)) return false;
  const at = v.indexOf("@");
  if (at <= 0 || at !== v.lastIndexOf("@") || at === v.length - 1) return false;
  const domain = v.slice(at + 1);
  return domain.includes(".") && !domain.startsWith(".") && !domain.endsWith(".");
}

export type FormDraft = Record<string, DraftValue>;

let rowSeq = 0;
export const newRowKey = () => `r${Date.now().toString(36)}${(rowSeq++).toString(36)}`;

function minutesToDuration(m: number): string {
  return `${Math.floor(m / 60)}:${String(m % 60).padStart(2, "0")}`;
}

/// "1:30" / "1h30" / "1u30" / "1.5" / "90m" / "2h" → minutes; null when
/// unparseable.
export function parseDuration(raw: string): number | null {
  const s = raw.trim().toLowerCase();
  if (s === "") return null;
  let m = /^(\d{1,5}):([0-5]\d)$/.exec(s);
  if (m) return Number(m[1]) * 60 + Number(m[2]);
  m = /^(\d{1,5})\s*[hu]\s*([0-5]?\d)\s*(m|min)?$/.exec(s);
  if (m) return Number(m[1]) * 60 + Number(m[2]);
  m = /^(\d{1,5})\s*m(in)?$/.exec(s);
  if (m) return Number(m[1]);
  m = /^(\d{1,4}(?:[.,]\d{1,2})?)\s*h?$/.exec(s);
  if (m) return Math.round(Number(m[1].replace(",", ".")) * 60);
  return null;
}

/// The picked orders whose label is still in the text (first per id) — the
/// same rule the server applies, so a deleted label drops its link.
export function liveOrders(text: string, orders: FormOrderRef[] | undefined): FormOrderRef[] {
  const seen = new Set<string>();
  return (orders ?? []).filter((o) => {
    if (seen.has(o.id) || !o.label || !text.includes(o.label)) return false;
    seen.add(o.id);
    return true;
  });
}

export type ShortTextSegment = { text: string; order?: FormOrderRef };

/// A short text with linked orders split into plain text and order pieces,
/// mirroring the server's note rendering: each order takes its label's first
/// occurrence not already taken by an earlier order.
export function shortTextSegments(text: string, orders: FormOrderRef[] | undefined): ShortTextSegment[] {
  const spans: { start: number; order: FormOrderRef }[] = [];
  for (const o of orders ?? []) {
    if (!o.label) continue;
    let at = text.indexOf(o.label);
    while (at >= 0 && spans.some((s) => at < s.start + s.order.label.length && s.start < at + o.label.length))
      at = text.indexOf(o.label, at + 1);
    if (at >= 0) spans.push({ start: at, order: o });
  }
  spans.sort((a, b) => a.start - b.start);
  const out: ShortTextSegment[] = [];
  let pos = 0;
  for (const s of spans) {
    if (s.start > pos) out.push({ text: text.slice(pos, s.start) });
    out.push({ text: s.order.label, order: s.order });
    pos = s.start + s.order.label.length;
  }
  if (pos < text.length) out.push({ text: text.slice(pos) });
  return out;
}

/// Plain text → paragraph HTML for the rich-text editor (escaped).
export function plainTextToHtml(text: string): string {
  if (text === "") return "";
  return text.split("\n").map((line) => `<p>${escapeHtml(line)}</p>`).join("");
}

/// Whether a rich-text value holds any visible text.
export function htmlHasText(html: string): boolean {
  return htmlToText(html).length > 0;
}

/// Empty draft for a template, with the admin's defaults applied. With
/// `tokens` (the ticket's resolved variables) `{{…}}` in a text default is
/// filled in; without (the admin preview) it stays visible.
export function emptyDraft(fields: FormField[], tokens?: Record<string, string>): FormDraft {
  const d: FormDraft = {};
  const text = (v: string | undefined) => (tokens ? substituteFormDefault(v ?? "", tokens) : v ?? "");
  for (const f of fields) {
    switch (f.type) {
      case "SectionHeader":
        break;
      case "Checkbox":
        d[f.id] = { kind: "bool", value: f.defaultValue === "true" };
        break;
      case "MultiSelect":
      case "CheckboxGroup":
        d[f.id] = { kind: "options", value: [] };
        break;
      case "Agent":
        d[f.id] = f.multiple ? { kind: "agents", value: [] } : { kind: "ref", value: null };
        break;
      case "Contact":
      case "Queue":
        d[f.id] = { kind: "ref", value: null };
        break;
      case "DateTime":
        d[f.id] = { kind: "datetime", value: null };
        break;
      case "Article":
        d[f.id] = { kind: "articles", value: [] };
        break;
      case "Invitees":
        d[f.id] = { kind: "invitees", value: [] };
        break;
      case "Attachments":
        d[f.id] = { kind: "files", value: [] };
        break;
      case "Address":
        d[f.id] = { kind: "text", value: text(f.defaultValue) };
        break;
      case "LongText":
        d[f.id] = { kind: "html", value: plainTextToHtml(text(f.defaultValue)) };
        break;
      case "ShortText":
        d[f.id] = { kind: "text", value: text(f.defaultValue) };
        break;
      default:
        d[f.id] = { kind: "text", value: f.defaultValue ?? "" };
    }
  }
  return d;
}

/// Draft from stored (normalized) answers — the edit path.
export function draftFromAnswers(fields: FormField[], answers: FormAnswers): FormDraft {
  const d = emptyDraft(fields);
  for (const f of fields) {
    const v = answers[f.id];
    if (f.type === "SectionHeader") continue;
    if (v === undefined || v === null) {
      // Defaults only apply to a fresh form; an unanswered field stays empty.
      d[f.id] = clearValue(d[f.id]);
      continue;
    }
    switch (f.type) {
      case "Checkbox":
        d[f.id] = { kind: "bool", value: v === true };
        break;
      case "MultiSelect":
      case "CheckboxGroup":
        d[f.id] = { kind: "options", value: Array.isArray(v) ? (v as string[]) : [] };
        break;
      case "Agent":
        if (f.multiple) {
          d[f.id] = {
            kind: "agents",
            value: Array.isArray(v) ? (v as AgentAnswer[])
              : typeof v === "object" && v && "id" in v ? [v as AgentAnswer] : [],
          };
          break;
        }
        d[f.id] = {
          kind: "ref",
          value: Array.isArray(v) ? ((v as AgentAnswer[])[0] ?? null)
            : typeof v === "object" ? (v as RefAnswer) : null,
        };
        break;
      case "Contact":
      case "Queue":
        d[f.id] = { kind: "ref", value: typeof v === "object" && !Array.isArray(v) ? (v as RefAnswer) : null };
        break;
      case "Invitees":
        d[f.id] = { kind: "invitees", value: Array.isArray(v) ? (v as InviteeAnswer[]) : [] };
        break;
      case "Attachments":
        d[f.id] = { kind: "files", value: Array.isArray(v) ? (v as AttachmentAnswer[]) : [] };
        break;
      case "DateTime":
        d[f.id] = { kind: "datetime", value: typeof v === "string" ? v : null };
        break;
      case "Duration":
        d[f.id] = { kind: "text", value: typeof v === "number" ? minutesToDuration(v) : "" };
        break;
      case "LongText":
        if (typeof v === "string") {
          const html = plainTextToHtml(v);
          d[f.id] = { kind: "html", value: html, legacy: { text: v, html } };
        } else {
          d[f.id] = { kind: "html", value: (v as { html?: string }).html ?? "" };
        }
        break;
      case "ShortText":
        if (typeof v === "object" && !Array.isArray(v) && "text" in v) {
          const a = v as ShortTextOrdersAnswer;
          d[f.id] = { kind: "text", value: a.text ?? "", orders: a.orders ?? [] };
        } else {
          d[f.id] = { kind: "text", value: String(v) };
        }
        break;
      case "Article":
        d[f.id] = {
          kind: "articles",
          value: (Array.isArray(v) ? (v as ArticleRowAnswer[]) : []).map((r) => ({
            key: newRowKey(),
            articleId: r.articleId,
            code: r.code ?? "",
            name: r.name ?? "",
            quantity: String(r.quantity),
          })),
        };
        break;
      default:
        d[f.id] = { kind: "text", value: String(v) };
    }
  }
  return d;
}

function clearValue(v: DraftValue | undefined): DraftValue {
  switch (v?.kind) {
    case "bool": return { kind: "bool", value: false };
    case "options": return { kind: "options", value: [] };
    case "ref": return { kind: "ref", value: null };
    case "datetime": return { kind: "datetime", value: null };
    case "articles": return { kind: "articles", value: [] };
    case "agents": return { kind: "agents", value: [] };
    case "invitees": return { kind: "invitees", value: [] };
    case "files": return { kind: "files", value: [] };
    case "html": return { kind: "html", value: "" };
    default: return { kind: "text", value: "" };
  }
}

const decimalString = (decimals: number) =>
  decimals === 0 ? /^-?\d+$/ : new RegExp(`^-?\\d+([.,]\\d{1,${decimals}})?$`);

/// Per-field Zod schema over the draft value; returns an English message.
function fieldSchema(f: FormField): z.ZodType<DraftValue> {
  const req = f.required;
  switch (f.type) {
    case "ShortText":
      return z.custom<DraftValue>((v) => !req || (v as { value: string }).value.trim().length > 0, {
        message: "Required.",
      });
    case "LongText":
      return z.custom<DraftValue>((v) => {
        if (!req) return true;
        const d = v as DraftValue;
        return d.kind === "html" ? htmlHasText(d.value) : d.kind === "text" && d.value.trim().length > 0;
      }, { message: "Required." });
    case "Number": {
      const decimals = f.decimals ?? 0;
      return z.custom<DraftValue>((v) => {
        const s = (v as { value: string }).value.trim();
        return s === "" ? !req : decimalString(decimals).test(s);
      }, { message: decimals === 0 ? "Enter a whole number." : `Enter a number (max ${decimals} decimals).` })
        .refine((v) => {
          const s = (v as { value: string }).value.trim().replace(",", ".");
          if (s === "") return true;
          const n = Number(s);
          return (f.min == null || n >= f.min) && (f.max == null || n <= f.max);
        }, { message: rangeMessage(f.min, f.max) });
    }
    case "Duration":
      return z.custom<DraftValue>((v) => {
        const s = (v as { value: string }).value;
        return s.trim() === "" ? !req : parseDuration(s) !== null;
      }, { message: "Use h:mm, e.g. 1:30." });
    case "Checkbox":
      return z.custom<DraftValue>((v) => !req || (v as { value: boolean }).value === true, { message: "Required." });
    case "MultiSelect":
    case "CheckboxGroup":
      return z.custom<DraftValue>((v) => !req || (v as { value: string[] }).value.length > 0, {
        message: "Pick at least one.",
      });
    case "Dropdown":
    case "Date":
      return z.custom<DraftValue>((v) => !req || (v as { value: string }).value !== "", { message: "Required." });
    case "DateTime":
      return z.custom<DraftValue>((v) => !req || !!(v as { value: string | null }).value, { message: "Required." });
    case "Agent":
      return z.custom<DraftValue>((v) => {
        const d = v as DraftValue;
        return !req || (d.kind === "agents" ? d.value.length > 0 : d.kind === "ref" && !!d.value);
      }, { message: "Required." });
    case "Contact":
    case "Queue":
      return z.custom<DraftValue>((v) => !req || !!(v as { value: RefAnswer | null }).value, { message: "Required." });
    case "Address":
      return z.custom<DraftValue>((v) => !req || (v as { value: string }).value.trim().length > 0, { message: "Required." })
        .refine((v) => (v as { value: string }).value.length <= 1000, { message: "At most 1000 characters." });
    case "Invitees":
      return z.custom<DraftValue>((v) => !req || (v as { value: InviteeAnswer[] }).value.length > 0, { message: "Add at least one person." })
        .refine((v) => (v as { value: InviteeAnswer[] }).value.every((p) => !!p.contactId || isEmail(p.email)), { message: "Check the e-mail addresses." });
    case "Attachments":
      return z.custom<DraftValue>((v) => !req || (v as { value: AttachmentAnswer[] }).value.length > 0, { message: "Add at least one file." });
    case "Article":
      return z.custom<DraftValue>((v) => {
        const rows = (v as { value: ArticleDraftRow[] }).value;
        return !req || rows.length > 0;
      }, { message: "Add at least one article." })
        .refine((v) => (v as { value: ArticleDraftRow[] }).value.every((r) => {
          const n = Number(r.quantity.replace(",", "."));
          return Number.isFinite(n) && n > 0 && (f.min == null || n >= f.min) && (f.max == null || n <= f.max);
        }), { message: `Every article needs a quantity${f.min != null || f.max != null ? ` (${rangeMessage(f.min, f.max).toLowerCase()})` : " above 0"}.` })
        .refine((v) => {
          const rows = (v as { value: ArticleDraftRow[] }).value;
          return (f.minRows == null || rows.length === 0 || rows.length >= f.minRows)
            && (f.maxRows == null || rows.length <= f.maxRows);
        }, { message: `Between ${f.minRows ?? 1} and ${f.maxRows ?? "∞"} articles.` });
    default:
      return z.custom<DraftValue>(() => true);
  }
}

function rangeMessage(min?: number | null, max?: number | null): string {
  if (min != null && max != null) return `Between ${min} and ${max}.`;
  if (min != null) return `At least ${min}.`;
  if (max != null) return `At most ${max}.`;
  return "Out of range.";
}

/// Field id → first error message. Empty object = valid.
export function validateDraft(fields: FormField[], draft: FormDraft): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const f of fields) {
    if (f.type === "SectionHeader") continue;
    if (!isFieldShown(f, draft)) continue;
    const v = draft[f.id];
    if (!v) continue;
    const r = fieldSchema(f).safeParse(v);
    if (!r.success) errors[f.id] = r.error.issues[0]?.message ?? "Invalid.";
  }
  return errors;
}

/// Draft → API payload. Empty values are omitted (= unanswered).
export function draftToInput(fields: FormField[], draft: FormDraft): FormAnswersInput {
  const out: FormAnswersInput = {};
  for (const f of fields) {
    const v = draft[f.id];
    if (!v || f.type === "SectionHeader") continue;
    if (!isFieldShown(f, draft)) continue;
    switch (v.kind) {
      case "text": {
        const s = v.value.trim();
        if (s === "") break;
        if (f.type === "Number") out[f.id] = Number(s.replace(",", "."));
        else if (f.type === "Duration") {
          const m = parseDuration(s);
          if (m !== null) out[f.id] = m;
        } else if (f.type === "ShortText") {
          const orders = liveOrders(s, v.orders);
          out[f.id] = orders.length > 0 ? { text: s, orders } : s;
        } else out[f.id] = f.type === "LongText" ? v.value.replace(/\s+$/, "") : s;
        break;
      }
      case "html":
        if (v.legacy && v.value === v.legacy.html) out[f.id] = v.legacy.text;
        else if (htmlHasText(v.value)) out[f.id] = { html: v.value };
        break;
      case "bool":
        out[f.id] = v.value;
        break;
      case "options":
        if (v.value.length > 0) out[f.id] = v.value;
        break;
      case "ref":
        if (v.value) out[f.id] = v.value.id;
        break;
      case "datetime":
        if (v.value) out[f.id] = v.value;
        break;
      case "articles":
        if (v.value.length > 0)
          out[f.id] = v.value.map((r) => ({ articleId: r.articleId, quantity: Number(r.quantity.replace(",", ".")) }));
        break;
      case "agents":
        if (v.value.length > 0)
          out[f.id] = { ids: v.value.map((a) => a.id), organizerId: v.value.find((a) => a.organizer)?.id ?? null };
        break;
      case "invitees":
        if (v.value.length > 0)
          out[f.id] = v.value.map((p) => (p.contactId ? { contactId: p.contactId } : { email: p.email.trim(), name: p.name.trim() }));
        break;
      case "files":
        if (v.value.length > 0) out[f.id] = v.value.map((a) => a.id);
        break;
    }
  }
  return out;
}
