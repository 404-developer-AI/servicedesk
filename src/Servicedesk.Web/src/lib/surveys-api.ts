// v0.0.38 Surveys API client.
//
// Mirrors the intake-forms-api split: admin CRUD + agent reads + a separate
// raw-fetch path for the public, CSRF-exempt token endpoints.

export type SurveyQuestionType =
  | "Rating"
  | "Nps"
  | "Text"
  | "SingleChoice"
  | "MultiChoice";

/** Where a question renders. `Survey` = once per response; `Agent` = once
 * per attributed agent on the ticket. Replaces the v0.0.38-pre tri-state
 * rating mode. */
export type SurveyQuestionScope = "Survey" | "Agent";

export type SurveyInvitationStatus =
  | "Sent"
  | "Submitted"
  | "Expired"
  | "Cancelled";

export interface SurveyQuestion {
  id: number;
  sortOrder: number;
  type: SurveyQuestionType;
  appliesTo: SurveyQuestionScope;
  label: string;
  helpText: string | null;
  isRequired: boolean;
  config: unknown;
}

export interface SurveySummary {
  id: string;
  name: string;
  description: string | null;
  isActive: boolean;
  ttlDays: number | null;
  questionCount: number;
  agentQuestionCount: number;
  invitationCount: number;
  responseCount: number;
  createdUtc: string;
  updatedUtc: string;
}

export interface Survey {
  id: string;
  name: string;
  description: string | null;
  introHtml: string;
  inviteSubject: string;
  inviteBodyHtml: string;
  isActive: boolean;
  ttlDays: number | null;
  /** Heading above the per-agent block on the public page. Empty/null =
   * heading is suppressed. */
  agentBlockHeading: string | null;
  /** Text on the Submit button on the public page. Required. */
  submitButtonLabel: string;
  /** Body shown after a successful submission. Required. */
  thankYouMessage: string;
  /** Body shown when the survey link has expired. Required. */
  expiredMessage: string;
  /** Body shown when the survey link is invalid. Required. */
  notFoundMessage: string;
  createdUtc: string;
  updatedUtc: string;
  questions: SurveyQuestion[];
}

export interface SurveyInvitationSummary {
  id: string;
  surveyId: string;
  surveyName: string;
  ticketId: string;
  ticketNumber: number;
  ticketSubject: string;
  status: SurveyInvitationStatus;
  sentToEmail: string;
  sentUtc: string;
  expiresUtc: string;
  submittedUtc: string | null;
  /** Display name of the requester contact (first + last). Null when the
   * contact row is missing. */
  contactName: string | null;
  /** Name of the requester contact's primary company (via
   * contact_companies.role='primary'). Null when none linked. */
  companyName: string | null;
}

export interface SurveyAttributedAgent {
  userId: string;
  displayName: string;
}

export interface SurveyPublicView {
  invitationId: string;
  surveyId: string;
  surveyName: string;
  introHtml: string;
  agentBlockHeading: string | null;
  submitButtonLabel: string;
  thankYouMessage: string;
  expiredMessage: string;
  notFoundMessage: string;
  status: SurveyInvitationStatus;
  expiresUtc: string;
  attributedAgents: SurveyAttributedAgent[];
  /** Survey-scope questions (asked once). */
  questions: SurveyQuestion[];
  /** Agent-scope questions (rendered once per attributed agent). */
  agentQuestions: SurveyQuestion[];
}

export interface SurveyResultsAggregate {
  surveyId: string;
  totalSent: number;
  totalSubmitted: number;
  totalExpired: number;
  totalCancelled: number;
  responseRate: number;
  agentLeaderboard: Array<{
    agentUserId: string;
    displayName: string;
    responseCount: number;
    averageRating: number | null;
  }>;
  questionAggregates: Array<{
    questionId: number;
    label: string;
    type: SurveyQuestionType;
    answerCount: number;
    averageNumeric: number | null;
    tally: Record<string, number> | null;
  }>;
  /** Per-agent breakdown for every Agent-scope question. */
  agentQuestionAggregates: Array<{
    questionId: number;
    label: string;
    type: SurveyQuestionType;
    agentUserId: string;
    agentDisplayName: string;
    answerCount: number;
    averageNumeric: number | null;
    tally: Record<string, number> | null;
  }>;
}

export interface SurveyResponseDetail {
  invitationId: string;
  ticketId: string;
  ticketNumber: number;
  ticketSubject: string;
  sentToEmail: string;
  sentUtc: string;
  submittedUtc: string;
  comment: string | null;
  surveySnapshot: unknown;
  answers: Array<{
    questionId: number;
    valueNumeric: number | null;
    valueText: string | null;
    valueJson: unknown | null;
  }>;
  agentAnswers: Array<{
    agentUserId: string;
    agentDisplayName: string;
    questionId: number;
    valueNumeric: number | null;
    valueText: string | null;
    valueJson: unknown | null;
  }>;
}

export interface SurveyUpsertInput {
  name: string;
  description: string | null;
  introHtml: string;
  inviteSubject: string;
  inviteBodyHtml: string;
  isActive: boolean;
  ttlDays: number | null;
  agentBlockHeading: string | null;
  submitButtonLabel: string;
  thankYouMessage: string;
  expiredMessage: string;
  notFoundMessage: string;
  questions: Array<{
    sortOrder: number;
    type: SurveyQuestionType;
    appliesTo: SurveyQuestionScope;
    label: string;
    helpText: string | null;
    isRequired: boolean;
    config: unknown;
  }>;
}

export interface PublicSurveySubmitInput {
  comment: string | null;
  answers: Array<{ questionId: number; value: unknown }>;
  agentAnswers: Array<{
    agentUserId: string;
    questionId: number;
    value: unknown;
  }>;
}

export interface RatingConfig {
  points: number;
  labels?: string[];
}
export interface ChoiceConfig {
  options: Array<{ value: string; label: string }>;
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
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
    },
    body: body === undefined ? undefined : JSON.stringify(body),
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

export const surveysApi = {
  list: (includeInactive = true) =>
    request<SurveySummary[]>(
      "GET",
      `/api/settings/surveys?includeInactive=${includeInactive ? "true" : "false"}`,
    ),
  get: (id: string) => request<Survey>("GET", `/api/settings/surveys/${id}`),
  create: (input: SurveyUpsertInput) =>
    request<Survey>("POST", "/api/settings/surveys", input),
  update: (id: string, input: SurveyUpsertInput) =>
    request<Survey>("PUT", `/api/settings/surveys/${id}`, input),
  remove: (id: string) =>
    request<void>("DELETE", `/api/settings/surveys/${id}`),
  cancelInvitation: (invitationId: string) =>
    request<void>(
      "POST",
      `/api/settings/surveys/invitations/${invitationId}/cancel`,
    ),
};

export const surveyResultsApi = {
  aggregate: (id: string) =>
    request<SurveyResultsAggregate>("GET", `/api/surveys/${id}/results`),
  invitations: (
    id: string,
    status?: SurveyInvitationStatus,
    limit?: number,
  ) => {
    const qs = new URLSearchParams();
    if (status) qs.set("status", status);
    if (limit) qs.set("limit", String(limit));
    const tail = qs.toString();
    return request<SurveyInvitationSummary[]>(
      "GET",
      `/api/surveys/${id}/invitations${tail ? `?${tail}` : ""}`,
    );
  },
  responseDetail: (invitationId: string) =>
    request<SurveyResponseDetail>(
      "GET",
      `/api/surveys/invitations/${invitationId}`,
    ),
};

export const surveysAgentApi = {
  usable: () =>
    request<
      Array<{
        id: string;
        name: string;
        ttlDays: number | null;
      }>
    >("GET", `/api/surveys/usable`),
  listForTicket: (ticketId: string) =>
    request<SurveyInvitationSummary[]>(
      "GET",
      `/api/tickets/${ticketId}/surveys`,
    ),
};

// Public — unauthenticated. CSRF-exempt server-side; plain fetch here so
// the token + JSON body land without an XSRF cookie present.
export const publicSurveysApi = {
  async get(token: string): Promise<SurveyPublicView> {
    const res = await fetch(
      `/api/public/surveys/${encodeURIComponent(token)}`,
      { method: "GET", credentials: "omit" },
    );
    if (!res.ok) {
      const err: Error & {
        status?: number;
        payload?: { status?: string; message?: string };
      } = new Error(`Survey fetch failed (${res.status}).`);
      err.status = res.status;
      try {
        err.payload = await res.json();
      } catch {
        /* ignore */
      }
      throw err;
    }
    return (await res.json()) as SurveyPublicView;
  },
  async submit(
    token: string,
    payload: PublicSurveySubmitInput,
  ): Promise<{ status: string; surveyName: string; message: string }> {
    const res = await fetch(
      `/api/public/surveys/${encodeURIComponent(token)}/submit`,
      {
        method: "POST",
        credentials: "omit",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      },
    );
    if (!res.ok) {
      const err: Error & { status?: number; payload?: { error?: string } } =
        new Error(`Survey submit failed (${res.status}).`);
      err.status = res.status;
      try {
        err.payload = await res.json();
      } catch {
        /* ignore */
      }
      throw err;
    }
    return (await res.json()) as {
      status: string;
      surveyName: string;
      message: string;
    };
  },
};
