import { csrfHeader } from "@/lib/csrf";
import { ApiError } from "@/lib/ticket-api";
import { memoizeTtl } from "@/lib/ttlCache";

/// v0.1.52 — ticket forms: admin-built interactive templates that agents
/// pick via `::` in the Note/Call composer, fill in and save as an internal
/// note. The server validates every answer against the form's frozen
/// snapshot; the client-side checks here are only for fast feedback.

export const FORM_FIELD_TYPES = [
  "SectionHeader",
  "ShortText",
  "LongText",
  "Number",
  "Dropdown",
  "MultiSelect",
  "Checkbox",
  "CheckboxGroup",
  "Date",
  "DateTime",
  "Duration",
  "Agent",
  "Contact",
  "Article",
] as const;
export type FormFieldType = (typeof FORM_FIELD_TYPES)[number];

export const FORM_FIELD_TYPE_LABEL: Record<FormFieldType, string> = {
  SectionHeader: "Section header",
  ShortText: "Short text",
  LongText: "Long text",
  Number: "Number",
  Dropdown: "Dropdown",
  MultiSelect: "Multi-select",
  Checkbox: "Checkbox (yes/no)",
  CheckboxGroup: "Checkbox list",
  Date: "Date",
  DateTime: "Date & time",
  Duration: "Duration",
  Agent: "Agent",
  Contact: "Contact",
  Article: "Articles + quantity",
};

export const hasOptions = (t: FormFieldType) =>
  t === "Dropdown" || t === "MultiSelect" || t === "CheckboxGroup";

export interface FormFieldOption {
  id: string;
  label: string;
}

export interface FormField {
  id: string;
  type: FormFieldType;
  label: string;
  help: string;
  required: boolean;
  width: "full" | "half";
  options?: FormFieldOption[] | null;
  min?: number | null;
  max?: number | null;
  decimals?: number | null;
  minRows?: number | null;
  maxRows?: number | null;
  defaultValue: string;
}

/// Stored (normalized) answer shapes, keyed by field id.
export type ArticleRowAnswer = { articleId: string; code?: string; name?: string; quantity: number };
export type RefAnswer = { id: string; name: string; email?: string };
export type FormAnswerValue =
  | string
  | number
  | boolean
  | string[]
  | RefAnswer
  | ArticleRowAnswer[];
export type FormAnswers = Record<string, FormAnswerValue>;

export interface FormTemplateSummary {
  id: string;
  name: string;
  description: string;
  isActive: boolean;
  queueIds: string[];
  fieldCount: number;
  createdUtc: string;
  updatedUtc: string;
}

export interface FormTemplateDetail extends FormTemplateSummary {
  fields: FormField[];
}

export interface FormTemplateUpsert {
  name: string;
  description: string;
  isActive: boolean;
  queueIds: string[];
  fields: FormField[];
}

export interface UsableFormTemplate {
  id: string;
  name: string;
  description: string;
  fieldCount: number;
}

export interface TicketFormSummary {
  id: string;
  ticketId: string;
  name: string;
  version: number;
  createdByUserId: string | null;
  createdByName: string | null;
  createdUtc: string;
  updatedByUserId: string | null;
  updatedByName: string | null;
  updatedUtc: string;
}

export interface TicketFormDetail extends TicketFormSummary {
  templateId: string | null;
  fields: FormField[];
  answers: FormAnswers;
  eventId: number | null;
}

export interface TicketFormVersion {
  version: number;
  answers: FormAnswers;
  eventId: number | null;
  savedByUserId: string | null;
  savedByName: string | null;
  savedUtc: string;
}

export interface FormArticleOption {
  id: string;
  code: string;
  name: string;
}

export interface FormContactOption {
  id: string;
  name: string;
  email: string;
}

/// Error payload of the form endpoints (`code` from FormRejectCode).
export type FormApiError = { error?: string; code?: string; fieldId?: string | null };

export function formApiError(err: unknown): FormApiError | null {
  if (err instanceof ApiError && err.body && typeof err.body === "object") return err.body as FormApiError;
  return null;
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const isSafe = method === "GET" || method === "HEAD";
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(isSafe ? {} : csrfHeader()),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let parsed: unknown = null;
    try {
      const txt = await res.text();
      if (txt) parsed = JSON.parse(txt);
    } catch {
      // non-JSON body
    }
    throw new ApiError(res.status, url, `${url} → ${res.status} ${res.statusText}`, parsed);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const enc = encodeURIComponent;

function fetchUsable(ticketId: string): Promise<UsableFormTemplate[]> {
  return request<{ items: UsableFormTemplate[] }>("GET", `/api/tickets/${enc(ticketId)}/forms/templates`).then(
    (r) => r.items,
  );
}

export const formTemplatesApi = {
  list: () =>
    request<{ items: FormTemplateSummary[] }>("GET", "/api/settings/form-templates").then((r) => r.items),
  get: (id: string) => request<FormTemplateDetail>("GET", `/api/settings/form-templates/${enc(id)}`),
  create: (body: FormTemplateUpsert) =>
    request<FormTemplateDetail>("POST", "/api/settings/form-templates", body),
  update: (id: string, body: FormTemplateUpsert) =>
    request<FormTemplateDetail>("PUT", `/api/settings/form-templates/${enc(id)}`, body),
  duplicate: (id: string) =>
    request<FormTemplateDetail>("POST", `/api/settings/form-templates/${enc(id)}/duplicate`),
  remove: (id: string) => request<void>("DELETE", `/api/settings/form-templates/${enc(id)}`),
};

export const ticketFormsApi = {
  list: (ticketId: string) =>
    request<{ items: TicketFormSummary[] }>("GET", `/api/tickets/${enc(ticketId)}/forms`).then((r) => r.items),
  usable: fetchUsable,
  /// `::` picker variant — one fetch per composer session (30 s TTL).
  usableCached: memoizeTtl(fetchUsable, 30_000),
  template: (ticketId: string, templateId: string) =>
    request<{ id: string; name: string; description: string; fields: FormField[] }>(
      "GET",
      `/api/tickets/${enc(ticketId)}/forms/templates/${enc(templateId)}`,
    ),
  create: (ticketId: string, templateId: string, answers: FormAnswersInput) =>
    request<TicketFormDetail>("POST", `/api/tickets/${enc(ticketId)}/forms`, { templateId, answers }),
  get: (formId: string) => request<TicketFormDetail>("GET", `/api/ticket-forms/${enc(formId)}`),
  versions: (formId: string) =>
    request<{ items: TicketFormVersion[] }>("GET", `/api/ticket-forms/${enc(formId)}/versions`).then(
      (r) => r.items,
    ),
  update: (formId: string, expectedVersion: number, answers: FormAnswersInput) =>
    request<TicketFormDetail>("PUT", `/api/ticket-forms/${enc(formId)}`, { expectedVersion, answers }),
  remove: (formId: string, reason: string) =>
    request<void>("POST", `/api/ticket-forms/${enc(formId)}/remove`, { reason }),
  articleOptions: (ticketId: string, search: string, scope: "contract" | "all") =>
    request<{ items: FormArticleOption[] }>(
      "GET",
      `/api/tickets/${enc(ticketId)}/forms/article-options?search=${enc(search)}&scope=${scope}`,
    ).then((r) => r.items),
  contactOptions: (ticketId: string, search: string) =>
    request<{ items: FormContactOption[] }>(
      "GET",
      `/api/tickets/${enc(ticketId)}/forms/contact-options?search=${enc(search)}`,
    ).then((r) => r.items),
};

/// What the client sends: ids only for references (the server resolves and
/// stores names), article rows as {articleId, quantity}.
export type FormAnswersInput = Record<string, string | number | boolean | string[] | { articleId: string; quantity: number }[]>;

/// Query keys under ["ticket", id] so the TicketUpdated realtime push
/// (useTicketRealtime) refreshes them with no extra wiring.
export const ticketFormKeys = {
  detail: (ticketId: string, formId: string) => ["ticket", ticketId, "forms", formId] as const,
  versions: (ticketId: string, formId: string) => ["ticket", ticketId, "forms", formId, "versions"] as const,
};

/// Window events: the `::` picker asks the ticket page to open the fill
/// dialog; anything can ask to open a filled form (pill / side panel / search).
export const FILL_FORM_EVENT = "sd:fill-form";
export const OPEN_FORM_EVENT = "sd:open-form";
export type FillFormDetail = { ticketId: string; templateId: string };
