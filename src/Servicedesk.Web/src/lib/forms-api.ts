import { ApiError, request } from "@/lib/http";
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
  "Queue",
  "Address",
  "Attachments",
  "Invitees",
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
  Queue: "Queue",
  Address: "Address",
  Attachments: "Attachments",
  Invitees: "People to invite",
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
  /** v0.1.58 — the name triggers use for this field (a-z, 0-9, _). */
  triggerKey?: string | null;
  /** v0.1.61 — Agent only: several agents may be picked. */
  multiple?: boolean | null;
  /** v0.1.61 — shown (and validated) only while this checkbox field is ticked. */
  showWhen?: string | null;
  /** v0.1.61 — inverts showWhen: shown only while that checkbox is not ticked. */
  showWhenUnchecked?: boolean | null;
  /** v0.1.61 — Duration only: the DateTime field it counts from (end = start + duration). */
  durationFrom?: string | null;
}

/// v0.1.61 — the "Outlook appointment" block of a template: which field
/// feeds which appointment property (field ids). Enforced server-side.
export interface FormOutlookMapping {
  enabled: boolean;
  titleFieldId?: string | null;
  startFieldId?: string | null;
  endFieldId?: string | null;
  techniciansFieldId?: string | null;
  addressFieldId?: string | null;
  teamsFieldId?: string | null;
  inviteesFieldId?: string | null;
  messageFieldId?: string | null;
  attachmentsFieldId?: string | null;
  shareAttachmentsFieldId?: string | null;
}

/// Stored shapes of the v0.1.61 field types.
export type AgentAnswer = { id: string; name: string; organizer?: boolean };
export type InviteeAnswer = { contactId?: string; email: string; name: string };
export type AttachmentAnswer = { id: string; name: string; size: number; contentType: string };

/// Same rule as the server (FormTemplateValidator.TriggerKeyPattern).
export const TRIGGER_KEY_PATTERN = /^[a-z][a-z0-9_]{0,39}$/;

/// Stored (normalized) answer shapes, keyed by field id.
export type ArticleRowAnswer = { articleId: string; code?: string; name?: string; quantity: number };
export type RefAnswer = { id: string; name: string; email?: string };
/// A rich-text LongText answer (v0.1.55); older answers are plain strings.
export type RichTextAnswer = { html: string };
/// An Adsolut order picked through `::` in a short text field: the order id
/// and the label as it appears in the text.
export type FormOrderRef = { id: string; label: string };
/// A short text answer with linked orders; without orders it is a string.
export type ShortTextOrdersAnswer = { text: string; orders: FormOrderRef[] };

export type FormAnswerValue =
  | string
  | RichTextAnswer
  | ShortTextOrdersAnswer
  | number
  | boolean
  | string[]
  | RefAnswer
  | ArticleRowAnswer[]
  | AgentAnswer[]
  | InviteeAnswer[]
  | AttachmentAnswer[];
export type FormAnswers = Record<string, FormAnswerValue>;

export interface FormTemplateSummary {
  id: string;
  name: string;
  description: string;
  isActive: boolean;
  queueIds: string[];
  fieldCount: number;
  /** Forms filled from this template land on the Back office forms list. */
  sendToBackoffice: boolean;
  /** v0.1.61 — back-office template: the form "Plan" opens. */
  boPlanTemplateId?: string | null;
  /** v0.1.61 — the template creates Outlook appointments. */
  outlookEnabled?: boolean;
  /** v0.1.61 — label of the button next to the ticket title; null = no button. */
  headerButtonLabel?: string | null;
  /** v0.1.61 — queues whose tickets show the header button. */
  headerButtonQueueIds?: string[];
  /** v0.1.61 — false: not offered through `::` (queueIds unused). */
  inPicker?: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface FormTemplateDetail extends FormTemplateSummary {
  fields: FormField[];
  outlook?: FormOutlookMapping | null;
}

export interface FormTemplateUpsert {
  name: string;
  description: string;
  isActive: boolean;
  queueIds: string[];
  fields: FormField[];
  sendToBackoffice: boolean;
  boPlanTemplateId?: string | null;
  outlook?: FormOutlookMapping | null;
  headerButtonLabel?: string | null;
  headerButtonQueueIds?: string[];
  inPicker?: boolean;
}

export interface UsableFormTemplate {
  id: string;
  name: string;
  description: string;
  fieldCount: number;
  /** v0.1.61 — set: the ticket shows a button with this label next to its title. */
  headerButtonLabel?: string | null;
  /** v0.1.61 — offered through `::` (and Insert form) on this ticket. */
  inPicker?: boolean;
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
  /** v0.1.54 — back office state (null = not a back office form). */
  boStatus?: "Open" | "Handled" | null;
  boHandledByName?: string | null;
  boHandledUtc?: string | null;
}

export interface TicketFormDetail extends TicketFormSummary {
  templateId: string | null;
  fields: FormField[];
  answers: FormAnswers;
  eventId: number | null;
  /** v0.1.61 — the live template's Outlook mapping (edit dialog helpers). */
  outlook?: FormOutlookMapping | null;
}

/// v0.1.61 — one Outlook appointment a form keeps in sync.
export interface FormCalendarEvent {
  id: string;
  userId: string;
  userName: string;
  isTeams: boolean;
  status: "Pending" | "Synced" | "Failed" | "CancelPending" | "Cancelled";
  webLink: string | null;
  joinUrl: string | null;
  attempts: number;
  nextAttemptUtc: string | null;
  lastError: string;
  updatedUtc: string;
}

export interface CalendarBusyItem {
  startUtc: string;
  endUtc: string;
  status: string;
  subject: string;
  location: string;
  isPrivate: boolean;
}

export interface CalendarAvailability {
  enabled: boolean;
  error?: string;
  users: { userId: string; name: string; reachable: boolean; items: CalendarBusyItem[] }[];
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
    request<{ id: string; name: string; description: string; fields: FormField[]; outlook?: FormOutlookMapping | null }>(
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
export type FormAnswersInput = Record<
  string,
  | string | number | boolean | string[] | RichTextAnswer | ShortTextOrdersAnswer
  | { articleId: string; quantity: number }[]
  /** v0.1.61 — several agents (Agent with "multiple"). */
  | { ids: string[]; organizerId: string | null }
  /** v0.1.61 — invitees: a contact, or a free address. */
  | ({ contactId: string } | { email: string; name: string })[]
>;

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
/// v0.1.61 — a form was saved (created or edited) on a ticket. The ticket
/// overlay listens to close itself after the planning form is saved.
export const FORM_SAVED_EVENT = "sd:form-saved";
export type FormSavedDetail = { ticketId: string; templateId: string | null; mode: "create" | "edit" };

/// v0.1.61 — Outlook calendar planning.
export const calendarApi = {
  settings: () => request<{ enabled: boolean }>("GET", "/api/settings/calendar"),
  /** Free/busy of the picked technicians (behind "Plan in calendars"). */
  availability: (userIds: string[], startUtc: string, endUtc: string, excludeFormId?: string | null) => {
    const q = new URLSearchParams({ userIds: userIds.join(","), start: startUtc, end: endUtc });
    if (excludeFormId) q.set("excludeFormId", excludeFormId);
    return request<CalendarAvailability>("GET", `/api/calendar/availability?${q.toString()}`);
  },
  status: (formId: string) =>
    request<{ items: FormCalendarEvent[] }>("GET", `/api/ticket-forms/${enc(formId)}/calendar`).then((r) => r.items),
  retry: (formId: string) => request<{ retried: number }>("POST", `/api/ticket-forms/${enc(formId)}/calendar/retry`),
};
