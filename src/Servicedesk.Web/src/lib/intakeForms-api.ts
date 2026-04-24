// v0.0.19 Intake Forms API client.
//
// Matches the shape of `slaApi` in `api.ts`: one object of named functions
// that wrap the shared `request` helper. Kept in its own file because the
// admin + agent + (public) surface is wide enough that co-locating it with
// the catch-all `api.ts` would make the import list unwieldy.
//
// Public endpoint (`/api/intake-forms/:token`) lives outside the authenticated
// SPA bundle — the `publicIntakeApi` helper uses raw `fetch` because the
// shared `request` pre-pends the CSRF header that the public path doesn't
// support (and the DoubleSubmitCsrfMiddleware explicitly exempts).

export type IntakeQuestionType =
  | "ShortText"
  | "LongText"
  | "DropdownSingle"
  | "DropdownMulti"
  | "Number"
  | "Date"
  | "YesNo"
  | "SectionHeader";

export type IntakeFormStatus =
  | "Draft"
  | "Sent"
  | "Submitted"
  | "Expired"
  | "Cancelled";

export interface IntakeQuestionOption {
  id: number;
  sortOrder: number;
  value: string;
  label: string;
}

export interface IntakeQuestion {
  id: number;
  sortOrder: number;
  type: IntakeQuestionType;
  label: string;
  helpText: string | null;
  isRequired: boolean;
  defaultValue: string | null;
  defaultToken: string | null;
  options: IntakeQuestionOption[];
}

export interface IntakeTemplate {
  id: string;
  name: string;
  description: string | null;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
  questions: IntakeQuestion[];
}

export interface IntakeFormInstanceSummary {
  id: string;
  templateId: string;
  templateName: string;
  status: IntakeFormStatus;
  expiresUtc: string | null;
  createdUtc: string;
  sentUtc: string | null;
  submittedUtc: string | null;
  sentToEmail: string | null;
}

export interface IntakeFormAgentView {
  instance: {
    id: string;
    templateId: string;
    ticketId: string;
    status: IntakeFormStatus;
    expiresUtc: string | null;
    createdUtc: string;
    sentUtc: string | null;
    submittedUtc: string | null;
    sentToEmail: string | null;
    prefill: Record<string, unknown>;
  };
  template: IntakeTemplate;
  /// Populated when status is Submitted: { [questionId]: answerValue }.
  /// Undefined/null for Draft/Sent/Expired/Cancelled.
  answers?: Record<string, unknown> | null;
}

export interface IntakePublicView {
  templateName: string;
  templateDescription: string | null;
  expiresUtc: string | null;
  questions: Array<{
    id: number;
    sortOrder: number;
    type: IntakeQuestionType;
    label: string;
    helpText: string | null;
    isRequired: boolean;
    options: Array<{ value: string; label: string }>;
  }>;
  prefill: Record<string, unknown>;
}

export interface QuestionInputDto {
  sortOrder: number;
  type: IntakeQuestionType;
  label: string;
  helpText: string | null;
  isRequired: boolean;
  defaultValue: string | null;
  defaultToken: string | null;
  options: Array<{ sortOrder: number; value: string; label: string }>;
}

export interface TemplateUpsertRequest {
  name: string;
  description: string | null;
  isActive?: boolean;
  questions: QuestionInputDto[];
}

// Token picker values the template designer can bind to a question's
// default. Must match IntakeTokens.Supported in the backend.
export const INTAKE_TOKENS = [
  "{{requester.name}}",
  "{{requester.email}}",
  "{{ticket.subject}}",
  "{{ticket.category}}",
  "{{ticket.number}}",
  "{{company.name}}",
] as const;
export type IntakeToken = (typeof INTAKE_TOKENS)[number];

// Shared authenticated request helper — same shape as api.ts
async function request<T>(
  method: string,
  url: string,
  body?: unknown,
  init?: RequestInit,
): Promise<T> {
  const csrf = document.cookie
    .split("; ")
    .find((c) => c.startsWith("XSRF-TOKEN="))
    ?.split("=")[1];
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      ...(csrf ? { "X-XSRF-TOKEN": decodeURIComponent(csrf) } : {}),
      ...(init?.headers ?? {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    ...init,
  });
  if (!res.ok) {
    let payload: unknown = null;
    try {
      payload = await res.json();
    } catch {
      /* ignore */
    }
    const err = new Error(`Request failed: ${res.status}`) as Error & {
      status?: number;
      payload?: unknown;
    };
    err.status = res.status;
    err.payload = payload;
    throw err;
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

export const intakeFormsApi = {
  // ─── Admin: template CRUD ───
  listTemplates: (includeInactive = false) =>
    request<IntakeTemplate[]>(
      "GET",
      `/api/settings/intake-templates${includeInactive ? "?includeInactive=true" : ""}`,
    ),
  getTemplate: (id: string) =>
    request<IntakeTemplate>("GET", `/api/settings/intake-templates/${id}`),
  createTemplate: (body: TemplateUpsertRequest) =>
    request<IntakeTemplate>("POST", "/api/settings/intake-templates", body),
  updateTemplate: (id: string, body: TemplateUpsertRequest) =>
    request<IntakeTemplate>("PUT", `/api/settings/intake-templates/${id}`, body),
  deactivateTemplate: (id: string) =>
    request<void>("DELETE", `/api/settings/intake-templates/${id}`),

  // ─── Agent: per-ticket instance management ───
  listForTicket: (ticketId: string) =>
    request<IntakeFormInstanceSummary[]>(
      "GET",
      `/api/tickets/${ticketId}/intake-forms`,
    ),
  getInstance: (ticketId: string, instanceId: string) =>
    request<IntakeFormAgentView>(
      "GET",
      `/api/tickets/${ticketId}/intake-forms/${instanceId}`,
    ),
  createDraft: (
    ticketId: string,
    body: { templateId: string; prefill?: Record<string, unknown> },
  ) =>
    request<IntakeFormAgentView>(
      "POST",
      `/api/tickets/${ticketId}/intake-forms`,
      body,
    ),
  updatePrefill: (
    ticketId: string,
    instanceId: string,
    prefill: Record<string, unknown>,
  ) =>
    request<void>(
      "PUT",
      `/api/tickets/${ticketId}/intake-forms/${instanceId}/prefill`,
      { prefill },
    ),
  deleteDraft: (ticketId: string, instanceId: string) =>
    request<void>(
      "DELETE",
      `/api/tickets/${ticketId}/intake-forms/${instanceId}`,
    ),
  resend: (ticketId: string, instanceId: string) =>
    request<IntakeFormAgentView>(
      "POST",
      `/api/tickets/${ticketId}/intake-forms/${instanceId}/resend`,
    ),
  /// URL for the agent-side PDF download. Returned as-is so the caller
  /// can feed it to a hidden <a download> or window.open — we never
  /// fetch-and-blob because that double-buffers the PDF in JS heap for
  /// no benefit. The server's Content-Disposition drives the filename.
  pdfUrl: (ticketId: string, instanceId: string) =>
    `/api/tickets/${ticketId}/intake-forms/${instanceId}/pdf`,
};

// ─── Public (no auth, CSRF-exempt) ───
// Uses plain fetch so no CSRF header is attached. Rate-limited server-side
// per (IP, token) via the `intake-public` policy.
export const publicIntakeApi = {
  get: async (token: string): Promise<IntakePublicView> => {
    const res = await fetch(`/api/intake-forms/${encodeURIComponent(token)}`, {
      credentials: "omit",
      headers: { Accept: "application/json" },
    });
    if (!res.ok) {
      const err = new Error(`Public intake fetch failed: ${res.status}`) as Error & {
        status?: number;
      };
      err.status = res.status;
      throw err;
    }
    return res.json();
  },
  submit: async (
    token: string,
    answers: Record<string, unknown>,
  ): Promise<{
    status: string;
    templateName: string;
    answers: Array<{ questionId: number; answer: unknown }>;
  }> => {
    const res = await fetch(
      `/api/intake-forms/${encodeURIComponent(token)}/submit`,
      {
        method: "POST",
        credentials: "omit",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ answers }),
      },
    );
    if (!res.ok) {
      let payload: unknown = null;
      try {
        payload = await res.json();
      } catch {
        /* ignore */
      }
      const err = new Error(`Public intake submit failed: ${res.status}`) as Error & {
        status?: number;
        payload?: unknown;
      };
      err.status = res.status;
      err.payload = payload;
      throw err;
    }
    return res.json();
  },
};
