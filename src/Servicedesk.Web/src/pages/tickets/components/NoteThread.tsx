import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { CornerDownRight, MessageSquareReply, Pencil, ThumbsUp, Trash2 } from "lucide-react";
import { RichTextEditor, splitMentionIds } from "@/components/RichTextEditor";
import { useConfirm } from "@/components/ConfirmDialog";
import { useAuth } from "@/auth/authStore";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { errorMessage } from "@/lib/http";
import { mentionApi } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";
import { useAttachmentUploads } from "../hooks/useAttachmentUploads";
import { TimelineBodyHtml } from "./timelinePreview";
import {
  buildReplyTree,
  noteThreadApi,
  noteThreadKey,
  type NoteLike,
  type NoteReply,
  type NoteThreadData,
  type ReplyNode,
} from "@/lib/note-threads-api";

/// Visual indent stops here; deeper replies keep this indent and show whom
/// they answer, so long threads never drift off the card.
const MAX_INDENT = 3;

const EMPTY: NoteThreadData = { replies: [], noteLikes: [], replyLikes: new Map() };

/// v0.1.58 — a reply may be just a pasted screenshot.
const isEmptyReply = (html: string) =>
  html.replace(/<[^>]*>/g, "").trim().length === 0 && !/<img\b/i.test(html);

const localPart = (email: string | null | undefined) => (email ? email.split("@")[0] : "someone");

/// v0.1.54 — the reply/like strip under an internal note or internal call,
/// plus its (nested) thread. Data comes from the ticket-wide note-threads
/// query; every mutation invalidates it and the server pushes TicketUpdated
/// so colleagues on the same ticket see it live.
export function NoteThread({
  ticketId,
  eventId,
  data,
  forceOpenReplyId,
}: {
  ticketId: string;
  eventId: number;
  data: NoteThreadData | undefined;
  /// Set when the URL points at a reply in this thread (#reply-{id}).
  forceOpenReplyId?: number | null;
}) {
  const thread = data ?? EMPTY;
  const tree = React.useMemo(() => buildReplyTree(thread.replies), [thread.replies]);
  const liveCount = thread.replies.filter((r) => !r.deleted).length;
  // Threads are open by default, whatever their length; "Hide replies"
  // collapses one by hand.
  const [open, setOpen] = React.useState(true);
  const [composerFor, setComposerFor] = React.useState<number | "note" | null>(null);

  React.useEffect(() => {
    if (forceOpenReplyId != null && thread.replies.some((r) => r.id === forceOpenReplyId)) setOpen(true);
  }, [forceOpenReplyId, thread.replies]);

  const byId = React.useMemo(() => new Map(thread.replies.map((r) => [r.id, r])), [thread.replies]);

  return (
    <div className="sd-note-thread mt-2 border-t border-glass/70 pt-2">
      <div className="flex flex-wrap items-center gap-1">
        <LikeButton ticketId={ticketId} likes={thread.noteLikes} target={{ eventId }} />
        <button type="button" onClick={() => { setComposerFor("note"); setOpen(true); }}
          className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground">
          <MessageSquareReply className="h-3.5 w-3.5" /> Reply
        </button>
        {thread.replies.length > 0 && (
          <button type="button" onClick={() => setOpen((o) => !o)}
            className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
            aria-expanded={open}>
            {open ? "Hide replies" : `${liveCount} ${liveCount === 1 ? "reply" : "replies"}`}
          </button>
        )}
      </div>

      {composerFor === "note" && (
        <ReplyComposer ticketId={ticketId} eventId={eventId} parentReplyId={null}
          onDone={() => setComposerFor(null)} />
      )}

      {open && tree.length > 0 && (
        <ul className="mt-1 space-y-1">
          {tree.map((n) => (
            <ReplyItem key={n.reply.id} node={n} level={1} ticketId={ticketId} eventId={eventId} thread={thread}
              byId={byId} composerFor={composerFor} setComposerFor={setComposerFor} />
          ))}
        </ul>
      )}
    </div>
  );
}

function ReplyItem({
  node, level, ticketId, eventId, thread, byId, composerFor, setComposerFor,
}: {
  node: ReplyNode;
  level: number;
  ticketId: string;
  eventId: number;
  thread: NoteThreadData;
  byId: Map<number, NoteReply>;
  composerFor: number | "note" | null;
  setComposerFor: (v: number | "note" | null) => void;
}) {
  const { user } = useAuth();
  const zone = useServerTimeZone();
  const confirm = useConfirm();
  const qc = useQueryClient();
  const r = node.reply;
  const [editing, setEditing] = React.useState(false);
  const mine = !!user && r.authorUserId === user.id;
  const canDelete = mine || user?.role === "Admin";
  const indented = level <= MAX_INDENT;
  const parent = r.parentReplyId != null ? byId.get(r.parentReplyId) : undefined;

  const remove = useMutation({
    mutationFn: () => noteThreadApi.remove(ticketId, r.id),
    onSuccess: () => void qc.invalidateQueries({ queryKey: noteThreadKey(ticketId) }),
    onError: (e) => toast.error(errorMessage(e, "Could not delete the reply")),
  });

  async function askDelete() {
    const ok = await confirm({
      title: "Delete reply?",
      description: "The reply is replaced by “Reply deleted”; replies to it stay.",
      confirmLabel: "Delete",
      tone: "destructive",
    });
    if (ok) remove.mutate();
  }

  return (
    <li>
      <div id={`reply-${r.id}`} className={cn(
        "sd-note-reply group/reply scroll-mt-24 rounded-md border border-glass bg-glass px-3 py-2 transition-shadow",
        indented && level > 1 && "ml-5",
      )}>
        <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px] text-muted-foreground">
          <span className="font-medium text-foreground">@{localPart(r.authorEmail)}</span>
          {!indented && parent && (
            <span className="inline-flex items-center gap-0.5"><CornerDownRight className="h-3 w-3" />@{localPart(parent.authorEmail)}</span>
          )}
          <span>{formatDateTime(r.createdUtc, zone)}</span>
          {r.editedUtc && !r.deleted && <span className="italic">edited</span>}
        </div>

        {r.deleted ? (
          <p className="mt-1 text-xs italic text-muted-foreground">Reply deleted</p>
        ) : editing ? (
          <ReplyEditor ticketId={ticketId} reply={r} onDone={() => setEditing(false)} />
        ) : (
          <TimelineBodyHtml html={r.bodyHtml} className="sd-note-reply-body rich-prose rich-prose-sm mt-1 text-sm" />
        )}

        {!r.deleted && !editing && (
          <div className="mt-1 flex flex-wrap items-center gap-1">
            <LikeButton ticketId={ticketId} likes={thread.replyLikes.get(r.id) ?? []} target={{ replyId: r.id }} small />
            <button type="button" onClick={() => setComposerFor(r.id)}
              className="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[11px] text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground">
              <MessageSquareReply className="h-3 w-3" /> Reply
            </button>
            {mine && (
              <button type="button" onClick={() => setEditing(true)} aria-label="Edit reply"
                className="rounded-md p-1 text-muted-foreground opacity-0 transition hover:bg-glass-hover hover:text-foreground focus:opacity-100 group-hover/reply:opacity-100">
                <Pencil className="h-3 w-3" />
              </button>
            )}
            {canDelete && (
              <button type="button" onClick={() => void askDelete()} aria-label="Delete reply"
                className="rounded-md p-1 text-muted-foreground opacity-0 transition hover:bg-glass-hover hover:text-destructive focus:opacity-100 group-hover/reply:opacity-100">
                <Trash2 className="h-3 w-3" />
              </button>
            )}
          </div>
        )}
      </div>

      {composerFor === r.id && (
        <div className={cn(level < MAX_INDENT && "ml-5")}>
          <ReplyComposer ticketId={ticketId} eventId={eventId} parentReplyId={r.id} onDone={() => setComposerFor(null)} />
        </div>
      )}

      {node.children.length > 0 && (
        <ul className={cn("mt-1 space-y-1", level < MAX_INDENT && "ml-5 border-l border-glass pl-0")}>
          {node.children.map((c) => (
            <ReplyItem key={c.reply.id} node={c} level={level + 1} ticketId={ticketId} eventId={eventId}
              thread={thread} byId={byId} composerFor={composerFor} setComposerFor={setComposerFor} />
          ))}
        </ul>
      )}
    </li>
  );
}

function LikeButton({
  ticketId, likes, target, small,
}: {
  ticketId: string;
  likes: NoteLike[];
  target: { eventId?: number; replyId?: number };
  small?: boolean;
}) {
  const { user } = useAuth();
  const qc = useQueryClient();
  const liked = !!user && likes.some((l) => l.userId === user.id);
  const toggle = useMutation({
    mutationFn: () => target.eventId != null
      ? noteThreadApi.likeNote(ticketId, target.eventId, !liked)
      : noteThreadApi.likeReply(ticketId, target.replyId!, !liked),
    onSettled: () => void qc.invalidateQueries({ queryKey: noteThreadKey(ticketId) }),
    onError: (e) => toast.error(errorMessage(e, "Could not save the like")),
  });
  const who = likes.map((l) => localPart(l.email)).join(", ");
  return (
    <button type="button" onClick={() => toggle.mutate()} disabled={toggle.isPending}
      aria-pressed={liked} title={who ? `Liked by ${who}` : "Like"}
      className={cn(
        "inline-flex items-center gap-1 rounded-md transition-colors hover:bg-glass-hover",
        small ? "px-1.5 py-0.5 text-[11px]" : "px-2 py-1 text-xs",
        liked ? "text-primary" : "text-muted-foreground hover:text-foreground",
      )}>
      <ThumbsUp className={cn(small ? "h-3 w-3" : "h-3.5 w-3.5", liked && "fill-current")} />
      {likes.length > 0 ? likes.length : "Like"}
    </button>
  );
}

function ReplyComposer({
  ticketId, eventId, parentReplyId, onDone,
}: {
  ticketId: string;
  eventId: number;
  parentReplyId: number | null;
  onDone: () => void;
}) {
  const qc = useQueryClient();
  const [html, setHtml] = React.useState("");
  const [mentions, setMentions] = React.useState<string[]>([]);
  // v0.1.58 — paste / drop / paperclip: images inline, other files as a
  // download link. The body is the source of truth; the server binds the
  // files it references to the reply on save.
  const uploads = useAttachmentUploads(ticketId);
  const send = useMutation({
    mutationFn: () => {
      const { userIds, groupIds } = splitMentionIds(mentions);
      return noteThreadApi.reply(ticketId, eventId, {
        parentReplyId,
        bodyHtml: html,
        mentionedUserIds: userIds.length ? userIds : undefined,
        mentionedGroupIds: groupIds.length ? groupIds : undefined,
      });
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: noteThreadKey(ticketId) });
      onDone();
    },
    onError: (e) => toast.error(errorMessage(e, "Could not send the reply")),
  });
  const empty = isEmptyReply(html);
  const blocked = empty || send.isPending || uploads.hasPending;

  return (
    <div className="sd-reply-composer mt-1.5 space-y-1.5"
      onKeyDown={(e) => {
        if (e.key === "Enter" && (e.ctrlKey || e.metaKey) && !blocked) {
          e.preventDefault();
          send.mutate();
        } else if (e.key === "Escape") onDone();
      }}>
      <RichTextEditor content="" onChange={setHtml} minHeight="44px" maxHeight="200px" autoFocus
        placeholder={parentReplyId ? "Write a reply… (paste a screenshot or drop a file)" : "Reply to this note… (@@ to tag, paste a screenshot or drop a file)"}
        onMentionQuery={(q) => mentionApi.search(q)} onMentionsChange={setMentions}
        onUploadFile={uploads.upload} linkNonImageUploads />
      <div className="flex items-center justify-end gap-2">
        <span className="mr-auto text-[10px] text-muted-foreground/70">
          {uploads.hasPending ? "Uploading…" : "Ctrl+Enter to send"}
        </span>
        <button type="button" onClick={onDone}
          className="rounded-md px-2.5 py-1 text-xs text-muted-foreground transition-colors hover:bg-glass-hover">
          Cancel
        </button>
        <button type="button" onClick={() => send.mutate()} disabled={blocked}
          className="rounded-md border border-primary/30 bg-primary/15 px-3 py-1 text-xs font-medium text-primary transition-colors hover:bg-primary/25 disabled:opacity-50">
          {send.isPending ? "Sending…" : "Reply"}
        </button>
      </div>
    </div>
  );
}

function ReplyEditor({ ticketId, reply, onDone }: { ticketId: string; reply: NoteReply; onDone: () => void }) {
  const qc = useQueryClient();
  const [html, setHtml] = React.useState(reply.bodyHtml);
  const uploads = useAttachmentUploads(ticketId);
  const save = useMutation({
    mutationFn: () => noteThreadApi.edit(ticketId, reply.id, html),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: noteThreadKey(ticketId) });
      onDone();
    },
    onError: (e) => toast.error(errorMessage(e, "Could not save the reply")),
  });
  return (
    <div className="mt-1 space-y-1.5">
      <RichTextEditor content={reply.bodyHtml} onChange={setHtml} minHeight="44px" maxHeight="200px" autoFocus preserveInlineNodes
        onUploadFile={uploads.upload} linkNonImageUploads />
      <div className="flex justify-end gap-2">
        <button type="button" onClick={onDone}
          className="rounded-md px-2.5 py-1 text-xs text-muted-foreground transition-colors hover:bg-glass-hover">Cancel</button>
        <button type="button" onClick={() => save.mutate()} disabled={save.isPending || uploads.hasPending || isEmptyReply(html)}
          className="rounded-md border border-primary/30 bg-primary/15 px-3 py-1 text-xs font-medium text-primary transition-colors hover:bg-primary/25 disabled:opacity-50">
          {save.isPending ? "Saving…" : "Save"}
        </button>
      </div>
    </div>
  );
}
