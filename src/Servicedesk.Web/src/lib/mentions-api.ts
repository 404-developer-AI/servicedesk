import { tagGroupApi } from "@/lib/group-tags-api";
import { taggingMailboxApi } from "@/lib/mailboxes-api";
import { userApi } from "@/lib/users-api";

/// Unified item the @@-mention popover renders. Agents and tagging mailboxes
/// are merged into one list; `kind` drives the badge + how the node id is
/// serialised (mailbox ids are prefixed `mbox:` so the submit-time split can
/// route them to mentionedMailboxIds without a node-schema change).
export type MentionPickerItem = {
  id: string;
  email: string;
  roleName: string;
  kind: "agent" | "mailbox" | "group";
  /// Display label; mailboxes use their friendly name, agents the email local-part.
  label?: string;
};

/// Merged @@-mention typeahead: agents first, then group tags, then tagging
/// mailboxes. Mailbox ids are prefixed `mbox:` and group ids `group:` so the
/// submit-time split routes them correctly.
export const mentionApi = {
  search: async (q: string): Promise<MentionPickerItem[]> => {
    const [agents, mailboxes, groups] = await Promise.all([
      userApi.searchAgents(q),
      taggingMailboxApi.searchForMention(q),
      tagGroupApi.searchForMention(q).catch(() => []),
    ]);
    const agentItems: MentionPickerItem[] = agents.map((a) => ({
      id: a.id,
      email: a.email,
      roleName: a.roleName,
      kind: "agent",
    }));
    const mailboxItems: MentionPickerItem[] = mailboxes.map((m) => ({
      id: `mbox:${m.id}`,
      email: m.email,
      roleName: "Mailbox",
      kind: "mailbox",
      label: m.name,
    }));
    const groupItems: MentionPickerItem[] = groups.map((g) => ({
      id: `group:${g.id}`,
      email: `${g.memberCount} member${g.memberCount === 1 ? "" : "s"}${g.personalAck ? " · each member confirms" : ""}`,
      roleName: "Group",
      kind: "group",
      label: g.name,
    }));
    return [...agentItems, ...groupItems, ...mailboxItems];
  },
};
