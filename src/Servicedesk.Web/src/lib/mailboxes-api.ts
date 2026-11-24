import { request } from "@/lib/http";

// ---- Tagging-only mailboxes (v0.0.60) ----

/// A login-less @@-mention target. Mentioning it mails a notification to its
/// address; it has no user row, role or tickets. Managed under Settings → Users.
export type TaggingMailbox = {
  id: string;
  name: string;
  email: string;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
};

export type TaggingMailboxInput = {
  name: string;
  email: string;
  isActive: boolean;
};

/// Slim row returned by the @@-picker search endpoint.
export type TaggingMailboxSearchItem = {
  id: string;
  name: string;
  email: string;
};

// ---- Tagging-only mailboxes (v0.0.60) -------------------------------

export const taggingMailboxApi = {
  // Admin-only CRUD, surfaced as the first card on Settings → Users.
  list: () => request<TaggingMailbox[]>("GET", "/api/settings/tagging-mailboxes"),
  create: (input: TaggingMailboxInput) =>
    request<TaggingMailbox>("POST", "/api/settings/tagging-mailboxes", input),
  update: (id: string, input: TaggingMailboxInput) =>
    request<TaggingMailbox>("PUT", `/api/settings/tagging-mailboxes/${id}`, input),
  remove: (id: string) =>
    request<void>("DELETE", `/api/settings/tagging-mailboxes/${id}`),
  /// Active-only typeahead for the @@-picker (agent-accessible).
  searchForMention: (q: string, limit = 20) => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    params.set("limit", String(limit));
    return request<TaggingMailboxSearchItem[]>(
      "GET",
      `/api/tagging-mailboxes/search?${params.toString()}`,
    );
  },
};

// ---- Inbound mailbox polling (v0.0.60, per-source since v0.0.66) -----

/// One row per inbound-mailbox source (a queue can have several). The toggle
/// pauses or resumes Graph polling for that source; delta-state is kept on
/// pause so resuming continues from where it left off.
export type InboundMailbox = {
  sourceId: string;
  queueId: string;
  queueName: string;
  mailbox: string;
  folderName: string | null;
  folderConfigured: boolean;
  isActive: boolean;
  pollingEnabled: boolean;
  lastPolledUtc: string | null;
  lastError: string | null;
  consecutiveFailures: number;
};

export const mailMailboxApi = {
  list: () => request<InboundMailbox[]>("GET", "/api/admin/mail/mailboxes"),
  setPolling: (sourceId: string, enabled: boolean) =>
    request<{ sourceId: string; pollingEnabled: boolean }>(
      "PUT",
      `/api/admin/mail/mailboxes/${sourceId}/polling`,
      { enabled },
    ),
};
