// API client for ticket templates — pre-canned ticket field sets the
// New-Ticket drawer applies in one click. Admin CRUD lives under
// /api/settings/ticket-templates; agents read the active list from
// /api/ticket-templates/usable. Token resolution for the
// subject/body/initial-note placeholders reuses the compose-templates
// resolve endpoint (see composeTemplates-api.ts).

export interface TicketTemplate {
  id: string;
  name: string;
  description: string | null;
  isActive: boolean;
  subject: string;
  bodyHtml: string;
  initialNoteHtml: string;
  initialNoteInternal: boolean;
  queueId: string | null;
  priorityId: string | null;
  statusId: string | null;
  categoryId: string | null;
  ticketTypeId: string | null;
  assigneeUserId: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface TicketTemplateUpsert {
  name: string;
  description: string | null;
  isActive: boolean;
  subject: string;
  bodyHtml: string;
  initialNoteHtml: string;
  initialNoteInternal: boolean;
  queueId: string | null;
  priorityId: string | null;
  statusId: string | null;
  categoryId: string | null;
  ticketTypeId: string | null;
  assigneeUserId: string | null;
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

export const ticketTemplatesApi = {
  list: (includeInactive = true) =>
    request<TicketTemplate[]>(
      "GET",
      `/api/settings/ticket-templates?includeInactive=${includeInactive}`,
    ),
  get: (id: string) =>
    request<TicketTemplate>("GET", `/api/settings/ticket-templates/${id}`),
  create: (body: TicketTemplateUpsert) =>
    request<TicketTemplate>("POST", "/api/settings/ticket-templates", body),
  update: (id: string, body: TicketTemplateUpsert) =>
    request<TicketTemplate>(
      "PUT",
      `/api/settings/ticket-templates/${id}`,
      body,
    ),
  remove: (id: string) =>
    request<void>("DELETE", `/api/settings/ticket-templates/${id}`),

  /// Active templates for the New-Ticket drawer picker.
  usable: () =>
    request<TicketTemplate[]>("GET", "/api/ticket-templates/usable"),
};
