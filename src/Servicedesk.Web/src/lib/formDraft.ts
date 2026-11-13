import { z } from "zod";
import type {
  ArticleRowAnswer,
  FormAnswers,
  FormAnswersInput,
  FormField,
  RefAnswer,
} from "@/lib/forms-api";

/// v0.1.52 — the in-dialog editing model for a ticket form, plus conversion
/// to the API payload and back from stored answers. Validation here is fast
/// feedback only; the server re-validates every answer against the form's
/// snapshot and is the authority.

export type ArticleDraftRow = { key: string; articleId: string; code: string; name: string; quantity: string };

export type DraftValue =
  | { kind: "text"; value: string }
  | { kind: "bool"; value: boolean }
  | { kind: "options"; value: string[] }
  | { kind: "ref"; value: RefAnswer | null }
  | { kind: "datetime"; value: string | null }
  | { kind: "articles"; value: ArticleDraftRow[] };

export type FormDraft = Record<string, DraftValue>;

let rowSeq = 0;
export const newRowKey = () => `r${Date.now().toString(36)}${(rowSeq++).toString(36)}`;

function minutesToDuration(m: number): string {
  return `${Math.floor(m / 60)}:${String(m % 60).padStart(2, "0")}`;
}

/// "1:30" / "1.5" / "90m" / "2h" → minutes; null when unparseable.
export function parseDuration(raw: string): number | null {
  const s = raw.trim().toLowerCase();
  if (s === "") return null;
  let m = /^(\d{1,5}):([0-5]\d)$/.exec(s);
  if (m) return Number(m[1]) * 60 + Number(m[2]);
  m = /^(\d{1,5})\s*m(in)?$/.exec(s);
  if (m) return Number(m[1]);
  m = /^(\d{1,4}(?:[.,]\d{1,2})?)\s*h?$/.exec(s);
  if (m) return Math.round(Number(m[1].replace(",", ".")) * 60);
  return null;
}

/// Empty draft for a template, with the admin's defaults applied.
export function emptyDraft(fields: FormField[]): FormDraft {
  const d: FormDraft = {};
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
      case "Contact":
        d[f.id] = { kind: "ref", value: null };
        break;
      case "DateTime":
        d[f.id] = { kind: "datetime", value: null };
        break;
      case "Article":
        d[f.id] = { kind: "articles", value: [] };
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
      case "Contact":
        d[f.id] = { kind: "ref", value: typeof v === "object" && !Array.isArray(v) ? (v as RefAnswer) : null };
        break;
      case "DateTime":
        d[f.id] = { kind: "datetime", value: typeof v === "string" ? v : null };
        break;
      case "Duration":
        d[f.id] = { kind: "text", value: typeof v === "number" ? minutesToDuration(v) : "" };
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
    case "LongText":
      return z.custom<DraftValue>((v) => !req || (v as { value: string }).value.trim().length > 0, {
        message: "Required.",
      });
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
    case "Contact":
      return z.custom<DraftValue>((v) => !req || !!(v as { value: RefAnswer | null }).value, { message: "Required." });
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
    switch (v.kind) {
      case "text": {
        const s = v.value.trim();
        if (s === "") break;
        if (f.type === "Number") out[f.id] = Number(s.replace(",", "."));
        else if (f.type === "Duration") {
          const m = parseDuration(s);
          if (m !== null) out[f.id] = m;
        } else out[f.id] = f.type === "LongText" ? v.value.replace(/\s+$/, "") : s;
        break;
      }
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
    }
  }
  return out;
}
