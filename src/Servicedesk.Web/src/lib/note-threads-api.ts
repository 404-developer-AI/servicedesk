import { request } from "@/lib/http";

/// v0.1.54 — replies and likes on internal notes / internal calls. Replies
/// nest to any depth (each carries its root note and its parent); the whole
/// ticket's threads come in one call and the tree is built client-side.

export interface NoteReply {
  id: number;
  rootEventId: number;
  parentReplyId: number | null;
  depth: number;
  authorUserId: string | null;
  authorEmail: string | null;
  bodyHtml: string;
  createdUtc: string;
  editedUtc: string | null;
  deleted: boolean;
}

export interface NoteLike {
  eventId: number | null;
  replyId: number | null;
  userId: string;
  email: string;
}

export interface NoteThreadsResponse {
  replies: NoteReply[];
  likes: NoteLike[];
}

export interface NoteReplyInput {
  parentReplyId?: number | null;
  bodyHtml: string;
  mentionedUserIds?: string[];
  mentionedGroupIds?: string[];
}

/// Under the ticket's query tree, so the existing TicketUpdated push
/// (invalidates ["ticket", id]) refreshes threads for everyone viewing it.
export const noteThreadKey = (ticketId: string) => ["ticket", ticketId, "note-threads"] as const;

const t = (ticketId: string) => `/api/tickets/${encodeURIComponent(ticketId)}`;

export const noteThreadApi = {
  get: (ticketId: string) => request<NoteThreadsResponse>("GET", `${t(ticketId)}/note-threads`),
  reply: (ticketId: string, eventId: number, input: NoteReplyInput) =>
    request<NoteReply>("POST", `${t(ticketId)}/events/${eventId}/replies`, input),
  edit: (ticketId: string, replyId: number, bodyHtml: string) =>
    request<NoteReply>("PUT", `${t(ticketId)}/replies/${replyId}`, { bodyHtml }),
  remove: (ticketId: string, replyId: number) => request<void>("DELETE", `${t(ticketId)}/replies/${replyId}`),
  likeNote: (ticketId: string, eventId: number, liked: boolean) =>
    request<void>(liked ? "PUT" : "DELETE", `${t(ticketId)}/events/${eventId}/like`),
  likeReply: (ticketId: string, replyId: number, liked: boolean) =>
    request<void>(liked ? "PUT" : "DELETE", `${t(ticketId)}/replies/${replyId}/like`),
};

/// Everything one note card needs: its replies (flat, id order) and the
/// likes on the note and on each of its replies.
export interface NoteThreadData {
  replies: NoteReply[];
  noteLikes: NoteLike[];
  replyLikes: Map<number, NoteLike[]>;
}

/// Groups the ticket-wide response per root note.
export function groupThreads(data: NoteThreadsResponse | undefined): Map<number, NoteThreadData> {
  const out = new Map<number, NoteThreadData>();
  if (!data) return out;
  const get = (eventId: number) => {
    let d = out.get(eventId);
    if (!d) out.set(eventId, (d = { replies: [], noteLikes: [], replyLikes: new Map() }));
    return d;
  };
  const rootOfReply = new Map<number, number>();
  for (const r of data.replies) {
    get(r.rootEventId).replies.push(r);
    rootOfReply.set(r.id, r.rootEventId);
  }
  for (const l of data.likes) {
    if (l.eventId != null) get(l.eventId).noteLikes.push(l);
    else if (l.replyId != null) {
      const root = rootOfReply.get(l.replyId);
      if (root == null) continue;
      const d = get(root);
      const list = d.replyLikes.get(l.replyId) ?? [];
      list.push(l);
      d.replyLikes.set(l.replyId, list);
    }
  }
  return out;
}

export interface ReplyNode {
  reply: NoteReply;
  children: ReplyNode[];
}

/// Builds the tree from parent links (any depth). Orphans — a parent that
/// is not in the list — are attached at the top so nothing ever disappears.
export function buildReplyTree(replies: NoteReply[]): ReplyNode[] {
  const nodes = new Map<number, ReplyNode>();
  for (const r of replies) nodes.set(r.id, { reply: r, children: [] });
  const roots: ReplyNode[] = [];
  for (const r of replies) {
    const node = nodes.get(r.id)!;
    const parent = r.parentReplyId != null ? nodes.get(r.parentReplyId) : undefined;
    if (parent) parent.children.push(node);
    else roots.push(node);
  }
  return roots;
}
