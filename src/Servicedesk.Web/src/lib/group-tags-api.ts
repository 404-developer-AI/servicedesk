import { request } from "@/lib/http";

/// v0.1.54 — group tags. An admin-defined group of agents that can be
/// @@-tagged like a person; the whole group shares ONE tag (never a mail)
/// that stays in every member's Group tags block until one member
/// acknowledges it.

export interface TagGroup {
  id: string;
  name: string;
  isActive: boolean;
  members: { userId: string; email: string }[];
  createdUtc: string;
  updatedUtc: string;
}

export interface TagGroupInput {
  name: string;
  isActive: boolean;
  memberIds: string[];
}

export interface TagGroupSearchItem {
  id: string;
  name: string;
  memberCount: number;
}

export interface GroupTag {
  id: string;
  groupId: string;
  groupName: string;
  ticketId: string;
  ticketNumber: number;
  ticketSubject: string;
  eventId: number;
  eventType: string;
  sourceUserEmail: string | null;
  previewText: string;
  createdUtc: string;
  ackedUtc: string | null;
  ackedByEmail: string | null;
}

export type GroupTagCursor = { createdUtc: string; id: string };

export const GROUP_TAGS_KEY = ["group-tags"] as const;

export const tagGroupApi = {
  // Admin — Settings → Users.
  list: () => request<TagGroup[]>("GET", "/api/settings/tag-groups"),
  create: (input: TagGroupInput) => request<TagGroup>("POST", "/api/settings/tag-groups", input),
  update: (id: string, input: TagGroupInput) =>
    request<TagGroup>("PUT", `/api/settings/tag-groups/${encodeURIComponent(id)}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/settings/tag-groups/${encodeURIComponent(id)}`),
  /// Active groups for the @@-picker (agent-accessible).
  searchForMention: (q: string, limit = 20) => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    params.set("limit", String(limit));
    return request<TagGroupSearchItem[]>("GET", `/api/tag-groups/search?${params.toString()}`);
  },
};

export const groupTagApi = {
  /// Open tags of the caller's groups; `isMember` hides the sidebar block
  /// for agents who belong to no active group.
  pending: () => request<{ isMember: boolean; items: GroupTag[] }>("GET", "/api/group-tags/pending"),
  history: (cursor: GroupTagCursor | null, limit = 50) => {
    const params = new URLSearchParams();
    if (cursor) {
      params.set("cursorUtc", cursor.createdUtc);
      params.set("cursorId", cursor.id);
    }
    params.set("limit", String(limit));
    return request<{ items: GroupTag[]; nextCursor: GroupTagCursor | null }>(
      "GET", `/api/group-tags/history?${params.toString()}`);
  },
  ack: (id: string) => request<{ acked: boolean }>("POST", `/api/group-tags/${encodeURIComponent(id)}/ack`),
};
