// API client for the Templates feature — pre-canned HTML snippets the
// agent pulls into an editor via the `::` picker (same trigger as intake
// forms; the picker merges results from both sources).

export interface ComposeTemplate {
  id: string;
  name: string;
  description: string | null;
  bodyHtml: string;
  isActive: boolean;
  /// Empty = available in every queue; otherwise the picker only surfaces
  /// the template when the editor's host ticket lives in one of these queues.
  queueIds: string[];
  /// v0.0.38 — optional CSAT survey that fires automatically when an agent
  /// sends a reply/note built from this template. Null = no survey-on-send.
  linkedSurveyId: string | null;
  createdUtc: string;
  updatedUtc: string;
}

/// Slim version returned by /api/compose-templates/usable. Drops the audit
/// timestamps that the picker doesn't render.
export interface UsableComposeTemplate {
  id: string;
  name: string;
  description: string | null;
  bodyHtml: string;
  queueIds: string[];
  linkedSurveyId: string | null;
}

export interface ComposeTemplateUpsert {
  name: string;
  description: string | null;
  bodyHtml: string;
  isActive?: boolean;
  queueIds: string[];
  linkedSurveyId: string | null;
}

async function request<T>(
  method: string,
  url: string,
  body?: unknown,
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

export interface ComposeTokenInfo {
  token: string;
  label: string;
}

export interface ResolveTokenParams {
  ticketId?: string | null;
  contactId?: string | null;
  companyId?: string | null;
}

export const composeTemplatesApi = {
  list: (includeInactive = true) =>
    request<ComposeTemplate[]>(
      "GET",
      `/api/settings/compose-templates?includeInactive=${includeInactive}`,
    ),
  get: (id: string) =>
    request<ComposeTemplate>("GET", `/api/settings/compose-templates/${id}`),
  create: (body: ComposeTemplateUpsert) =>
    request<ComposeTemplate>("POST", "/api/settings/compose-templates", body),
  update: (id: string, body: ComposeTemplateUpsert) =>
    request<ComposeTemplate>(
      "PUT",
      `/api/settings/compose-templates/${id}`,
      body,
    ),
  remove: (id: string) =>
    request<void>("DELETE", `/api/settings/compose-templates/${id}`),

  /// Picker endpoint. `queueId=null` lists only unrestricted templates —
  /// matches the New-Ticket drawer where no queue is selected yet.
  usable: (queueId: string | null) =>
    request<UsableComposeTemplate[]>(
      "GET",
      queueId
        ? `/api/compose-templates/usable?queueId=${encodeURIComponent(queueId)}`
        : "/api/compose-templates/usable",
    ),

  /// Token picker metadata for the admin editor. Static list — fetched once
  /// per session and cached aggressively by React Query.
  listTokens: () =>
    request<{ tokens: ComposeTokenInfo[] }>(
      "GET",
      "/api/settings/compose-templates/tokens",
    ),

  /// Resolve `{{...}}` placeholders for the current compose context.
  /// At least one of ticketId / contactId should be set; an empty call
  /// returns every supported token mapped to "".
  resolveTokens: (params: ResolveTokenParams) => {
    const qs = new URLSearchParams();
    if (params.ticketId) qs.set("ticketId", params.ticketId);
    if (params.contactId) qs.set("contactId", params.contactId);
    if (params.companyId) qs.set("companyId", params.companyId);
    const suffix = qs.toString();
    return request<{ tokens: Record<string, string> }>(
      "GET",
      suffix
        ? `/api/compose-templates/resolve?${suffix}`
        : "/api/compose-templates/resolve",
    );
  },
};
