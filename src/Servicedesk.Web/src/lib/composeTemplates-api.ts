import { request } from "@/lib/http";
import { memoizeTtl } from "@/lib/ttlCache";

/// How long the `::` picker reuses a fetched template list (v0.1.4).
const PICKER_CACHE_TTL_MS = 30_000;

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
  /// v0.0.42 — Empty = any status; otherwise the picker (and the auto-insert
  /// matcher) only surfaces the template when the ticket's current status is
  /// in this list. ANDed with queueIds.
  statusIds: string[];
  /// v0.0.42 — When true, this template is auto-prefilled into the
  /// "Write an internal note" composer whenever the agent opens an empty
  /// composer on a ticket that matches this template's queue + status scope.
  /// Tie-breaker between multiple matching templates: most-recently-updated.
  autoInsertOnNote: boolean;
  /// v0.1.17 — which composer buttons offer this template (Note / Mail /
  /// Call), plus auto-insert into an empty Call composer.
  useForNote: boolean;
  useForMail: boolean;
  useForCall: boolean;
  autoInsertOnCall: boolean;
  /// v0.1.32 — Insights Workflow roles (read at report time).
  workflowClose: boolean;
  specialistConsult: boolean;
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
  statusIds: string[];
  autoInsertOnNote: boolean;
  autoInsertOnCall?: boolean;
  linkedSurveyId: string | null;
}

/// v0.1.17 — the composer a template is requested for.
export type ComposeTemplateKind = "note" | "mail" | "call";

export interface ComposeTemplateUpsert {
  name: string;
  description: string | null;
  bodyHtml: string;
  isActive?: boolean;
  queueIds: string[];
  statusIds: string[];
  autoInsertOnNote: boolean;
  useForNote: boolean;
  useForMail: boolean;
  useForCall: boolean;
  autoInsertOnCall: boolean;
  workflowClose: boolean;
  specialistConsult: boolean;
  linkedSurveyId: string | null;
}

export interface ComposeTokenInfo {
  token: string;
  label: string;
}

/// Upload result for an inline template image (v0.0.92). Shape matches
/// TicketAttachmentMeta so the RichTextEditor's paste/drop pipeline can
/// consume it unchanged; `url` is the agent-readable download endpoint the
/// template body embeds. At send time the backend swaps it for a real
/// inline mail attachment (cid), so recipients see the image too.
export interface ComposeTemplateImageMeta {
  id: string;
  url: string;
  mimeType: string;
  size: number;
  filename: string;
}

export interface ResolveTokenParams {
  ticketId?: string | null;
  contactId?: string | null;
  companyId?: string | null;
}

/// Picker endpoint. `queueId=null` lists only unrestricted templates —
/// matches the New-Ticket drawer where no queue is selected yet.
/// v0.0.42: `statusId` narrows the result further when supplied.
/// Standalone (not an object member) so the memoised variant below can
/// reference it without the object referencing itself in its initializer.
/// v0.1.17: `kind` keeps only templates scoped for that composer.
function fetchUsable(
  queueId: string | null,
  statusId?: string | null,
  kind?: ComposeTemplateKind,
): Promise<UsableComposeTemplate[]> {
  const qs = new URLSearchParams();
  if (queueId) qs.set("queueId", queueId);
  if (statusId) qs.set("statusId", statusId);
  if (kind) qs.set("kind", kind);
  const suffix = qs.toString();
  return request<UsableComposeTemplate[]>(
    "GET",
    suffix
      ? `/api/compose-templates/usable?${suffix}`
      : "/api/compose-templates/usable",
  );
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

  usable: fetchUsable,

  /// v0.1.4 — picker variant of `usable`: memoised for a short TTL so the
  /// `::` picker (one call per keystroke before the editor-side debounce)
  /// fetches the list once per composer session instead of per key. Admin
  /// CRUD keeps using `usable`/`list`, so a fresh edit is never masked there.
  usableCached: memoizeTtl(fetchUsable, PICKER_CACHE_TTL_MS),

  /// v0.0.42 — Auto-insert lookup. Returns the single best-matching
  /// auto-insert template (if any) for the (queue, status) tuple of an
  /// internal-note composer that's about to be opened empty. `null` =
  /// no matching template, leave the composer blank.
  /// v0.1.17 — same lookup for the Call composer.
  defaultForCall: (queueId: string, statusId: string) =>
    request<{ template: UsableComposeTemplate | null }>(
      "GET",
      `/api/compose-templates/default-for-call?queueId=${encodeURIComponent(
        queueId,
      )}&statusId=${encodeURIComponent(statusId)}`,
    ),

  defaultForNote: (queueId: string, statusId: string) =>
    request<{ template: UsableComposeTemplate | null }>(
      "GET",
      `/api/compose-templates/default-for-note?queueId=${encodeURIComponent(
        queueId,
      )}&statusId=${encodeURIComponent(statusId)}`,
    ),

  /// Inline-image upload for the admin template editor (multipart).
  /// Images only — the server sniffs
  /// the real MIME type and rejects everything else.
  uploadImage: async (file: File): Promise<ComposeTemplateImageMeta> => {
    const fd = new FormData();
    fd.append("file", file, file.name);
    return request<ComposeTemplateImageMeta>("POST", "/api/settings/compose-templates/images", fd);
  },

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
