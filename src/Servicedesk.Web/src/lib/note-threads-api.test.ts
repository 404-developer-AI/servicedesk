import { describe, expect, it } from "vitest";
import { buildReplyTree, groupThreads, type NoteReply } from "./note-threads-api";

const reply = (id: number, root: number, parent: number | null, depth = 1): NoteReply => ({
  id, rootEventId: root, parentReplyId: parent, depth, authorUserId: null, authorEmail: "agent@example.test",
  bodyHtml: `<p>${id}</p>`, createdUtc: "2026-11-14T10:00:00Z", editedUtc: null, deleted: false,
});

describe("note threads", () => {
  it("builds a tree of any depth from parent links", () => {
    const tree = buildReplyTree([reply(1, 10, null), reply(2, 10, 1, 2), reply(3, 10, 2, 3), reply(4, 10, 3, 4), reply(5, 10, null)]);
    expect(tree.map((n) => n.reply.id)).toEqual([1, 5]);
    expect(tree[0].children[0].children[0].children[0].reply.id).toBe(4);
  });

  it("keeps a reply whose parent is missing at the top instead of dropping it", () => {
    expect(buildReplyTree([reply(7, 10, 99)]).map((n) => n.reply.id)).toEqual([7]);
  });

  it("groups replies and likes per root note", () => {
    const grouped = groupThreads({
      replies: [reply(1, 10, null), reply(2, 20, null)],
      likes: [
        { eventId: 10, replyId: null, userId: "u1", email: "a@example.test" },
        { eventId: null, replyId: 2, userId: "u2", email: "b@example.test" },
      ],
    });
    expect(grouped.get(10)?.replies.map((r) => r.id)).toEqual([1]);
    expect(grouped.get(10)?.noteLikes).toHaveLength(1);
    expect(grouped.get(20)?.replyLikes.get(2)).toHaveLength(1);
  });
});
