import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Reply, ReplyAll, Forward as ForwardIcon, Mail } from "lucide-react";
import {
  ticketApi,
  userApi,
  type MailRecipientInput,
  type OutboundMailKind,
} from "@/lib/ticket-api";
import { RichTextEditor } from "@/components/RichTextEditor";
import { cn } from "@/lib/utils";
import type { PendingMailAction } from "@/stores/useWorkspaceStore";
import { AttachmentTray } from "./AttachmentTray";
import { useAttachmentUploads } from "../hooks/useAttachmentUploads";
import { intakeFormsApi } from "@/lib/intakeForms-api";
import { IntakePrefillDrawer } from "@/components/intake/IntakePrefillDrawer";

export type MailRecipient = { address: string; name: string };

export type MailContext = {
  ticketSubject: string;
  ticketNumber: number;
  latestInbound: {
    from: MailRecipient | null;
    to: MailRecipient[];
    cc: MailRecipient[];
    subject: string | null;
    bodyHtml: string | null;
    receivedUtc: string | null;
  } | null;
  requesterEmail: string | null;
  ownMailboxAddresses: string[];
};

type Props = {
  ticketId: string;
  context: MailContext;
  initialIntent: PendingMailAction | null;
  onSent: () => void;
  onCancel: () => void;
};

/// Normalise an email for dedup + own-mailbox matching. Lowercases the
/// whole address and strips any `+suffix` on the local-part so plus-address
/// variants of a queue mailbox (e.g. `support+TCK-42@example.com`) collide
/// with the bare address (`support@example.com`). Without this the
/// "reply-all" flow would echo the plus-addressed Reply-To of the inbound
/// mail back into Cc, which is never what the agent wants.
function normalizeAddress(addr: string): string {
  const lower = addr.trim().toLowerCase();
  const at = lower.indexOf("@");
  if (at <= 0) return lower;
  const local = lower.slice(0, at);
  const domain = lower.slice(at + 1);
  const plus = local.indexOf("+");
  const baseLocal = plus >= 0 ? local.slice(0, plus) : local;
  return `${baseLocal}@${domain}`;
}

function parseAddresses(raw: string): MailRecipientInput[] {
  if (!raw.trim()) return [];
  return raw
    .split(/[,;\n]+/)
    .map((s) => s.trim())
    .filter(Boolean)
    .map((s) => {
      const match = s.match(/^"?([^"<]+?)"?\s*<([^>]+)>$/);
      if (match) return { address: match[2].trim(), name: match[1].trim() };
      return { address: s };
    });
}

function formatAddresses(list: MailRecipient[]): string {
  return list.map((r) => (r.name && r.name !== r.address ? `${r.name} <${r.address}>` : r.address)).join(", ");
}

function ensureRePrefix(subject: string): string {
  if (!subject) return "";
  return /^re:\s*/i.test(subject) ? subject : `Re: ${subject}`;
}

function ensureFwdPrefix(subject: string): string {
  if (!subject) return "";
  return /^(fw|fwd):\s*/i.test(subject) ? subject : `Fwd: ${subject}`;
}

function ensureTicketTag(subject: string, ticketNumber: number): string {
  const tag = `[#${ticketNumber}]`;
  if (/\[#\d+\]/.test(subject)) return subject;
  return subject ? `${subject} ${tag}` : tag;
}

function buildForwardQuote(source: {
  from: { address: string; name: string } | null;
  subject: string | null;
  receivedUtc: string | null;
  bodyHtml: string | null;
}): string {
  const fromLine = source.from
    ? `${source.from.name || source.from.address} &lt;${source.from.address}&gt;`
    : "(unknown sender)";
  const dateLine = source.receivedUtc
    ? new Date(source.receivedUtc).toLocaleString()
    : "";
  const subjectLine = source.subject ?? "";
  const body = source.bodyHtml ?? "";
  return `<p></p><p>---------- Forwarded message ----------</p>
<p><strong>From:</strong> ${fromLine}<br>${dateLine ? `<strong>Date:</strong> ${dateLine}<br>` : ""}<strong>Subject:</strong> ${subjectLine}</p>
<blockquote>${body}</blockquote>`;
}

function buildReplyQuote(source: {
  from: { address: string; name: string } | null;
  receivedUtc: string | null;
  bodyHtml: string | null;
}): string {
  const body = source.bodyHtml ?? "";
  if (!body.trim()) return "";
  const who = source.from
    ? `${source.from.name || source.from.address} &lt;${source.from.address}&gt;`
    : "(unknown sender)";
  const when = source.receivedUtc
    ? new Date(source.receivedUtc).toLocaleString()
    : "";
  const preamble = when
    ? `On ${when}, ${who} wrote:`
    : `${who} wrote:`;
  // Two empty paragraphs so the cursor lands above the quote and the agent
  // can start typing immediately without having to nudge the quote down.
  return `<p></p><p></p><p>${preamble}</p>
<blockquote>${body}</blockquote>`;
}

export function SendMailForm({ ticketId, context, initialIntent, onSent, onCancel }: Props) {
  const [kind, setKind] = React.useState<OutboundMailKind>(
    initialIntent?.kind ?? (context.latestInbound ? "Reply" : "New"),
  );
  const [to, setTo] = React.useState("");
  const [cc, setCc] = React.useState("");
  const [bcc, setBcc] = React.useState("");
  const [subject, setSubject] = React.useState("");
  const [bodyHtml, setBodyHtml] = React.useState("");
  const [initialEditorContent, setInitialEditorContent] = React.useState<
    string | undefined
  >(undefined);
  const [editorKey, setEditorKey] = React.useState(0);
  const [showCc, setShowCc] = React.useState(false);
  const [showBcc, setShowBcc] = React.useState(false);
  const [mentionedUserIds, setMentionedUserIds] = React.useState<string[]>([]);
  const [linkedFormIds, setLinkedFormIds] = React.useState<string[]>([]);
  const [drawerInstanceId, setDrawerInstanceId] = React.useState<string | null>(
    null,
  );
  const queryClient = useQueryClient();
  const attachments = useAttachmentUploads(ticketId);

  const applyKind = React.useCallback(
    (
      nextKind: OutboundMailKind,
      override?: PendingMailAction["source"] | null,
    ) => {
      setKind(nextKind);
      // Override is the event the agent clicked Reply/Reply-all/Forward on;
      // if absent we fall back to the ticket's latest inbound.
      const source =
        override ??
        (context.latestInbound
          ? {
              from: context.latestInbound.from,
              to: context.latestInbound.to,
              cc: context.latestInbound.cc,
              subject: context.latestInbound.subject,
              bodyHtml: context.latestInbound.bodyHtml,
              receivedUtc: context.latestInbound.receivedUtc,
              isOutbound: false,
            }
          : null);
      const ownSet = new Set(context.ownMailboxAddresses.map(normalizeAddress));
      const dedupe = (list: MailRecipient[]): MailRecipient[] => {
        const seen = new Set<string>();
        const out: MailRecipient[] = [];
        for (const r of list) {
          const key = normalizeAddress(r.address);
          if (!key || seen.has(key) || ownSet.has(key)) continue;
          seen.add(key);
          out.push(r);
        }
        return out;
      };

      if (nextKind === "New") {
        setTo(context.requesterEmail ? context.requesterEmail : "");
        setCc("");
        setBcc("");
        setSubject(ensureTicketTag(context.ticketSubject, context.ticketNumber));
        setInitialEditorContent(undefined);
        setBodyHtml("");
        setEditorKey((k) => k + 1);
        return;
      }

      if (nextKind === "Forward") {
        setTo("");
        setCc("");
        setBcc("");
        const baseSubject = source?.subject ?? context.ticketSubject;
        setSubject(ensureTicketTag(ensureFwdPrefix(baseSubject), context.ticketNumber));
        const quote = source ? buildForwardQuote(source) : "";
        setInitialEditorContent(quote);
        setBodyHtml(quote);
        setEditorKey((k) => k + 1);
        return;
      }

      if (!source) return;

      // On inbound: reply goes back to the external sender; reply-all also
      // picks up the other to/cc. On outbound (following up on our own sent
      // mail): the audience is already source.to/source.cc — don't reply to
      // our own mailbox.
      const replyTargets = source.isOutbound
        ? source.to
        : source.from
          ? [source.from]
          : [];
      const replyAllCc = source.isOutbound
        ? source.cc
        : [...source.to, ...source.cc];

      setTo(formatAddresses(dedupe(replyTargets)));
      if (nextKind === "ReplyAll") {
        const ccList = dedupe(replyAllCc);
        setCc(formatAddresses(ccList));
        if (ccList.length > 0) setShowCc(true);
      } else {
        setCc("");
      }
      setBcc("");
      setSubject(
        ensureTicketTag(
          ensureRePrefix(source.subject ?? context.ticketSubject),
          context.ticketNumber,
        ),
      );
      const quote = buildReplyQuote(source);
      setInitialEditorContent(quote || undefined);
      setBodyHtml(quote);
      setEditorKey((k) => k + 1);
    },
    [context],
  );

  // Initial pre-fill on mount + whenever a new pending-intent arrives (the
  // `id` is monotonic so clicking the same event twice still re-applies).
  React.useEffect(() => {
    if (initialIntent) {
      applyKind(initialIntent.kind, initialIntent.source);
    } else {
      applyKind(context.latestInbound ? "Reply" : "New");
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initialIntent?.id]);

  const mutation = useMutation({
    mutationFn: () =>
      ticketApi.sendMail(ticketId, {
        kind,
        to: parseAddresses(to),
        cc: parseAddresses(cc),
        bcc: parseAddresses(bcc),
        subject: subject.trim(),
        bodyHtml,
        attachmentIds: attachments.readyAttachmentIds,
        mentionedUserIds: mentionedUserIds.length > 0 ? mentionedUserIds : undefined,
        linkedFormIds: linkedFormIds.length > 0 ? linkedFormIds : undefined,
      }),
    onSuccess: () => {
      toast.success("Mail sent");
      setBodyHtml("");
      setMentionedUserIds([]);
      setLinkedFormIds([]);
      attachments.reset();
      setEditorKey((k) => k + 1);
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      onSent();
    },
    onError: (err: unknown) => {
      const message =
        err instanceof Error && err.message ? err.message : "Failed to send mail";
      toast.error(message);
    },
  });

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (parseAddresses(to).length === 0 && parseAddresses(cc).length === 0 && parseAddresses(bcc).length === 0) {
      toast.error("At least one recipient is required");
      return;
    }
    if (!subject.trim()) {
      toast.error("Subject is required");
      return;
    }
    if (!bodyHtml.trim() || bodyHtml === "<p></p>") {
      toast.error("Please write a message before sending");
      return;
    }
    if (attachments.hasPending) {
      toast.error("Wait for attachment uploads to finish");
      return;
    }
    mutation.mutate();
  }

  const canReply = context.latestInbound !== null;

  return (
    <form onSubmit={handleSubmit} className="space-y-3">
      <div className="flex items-center gap-1">
        <button
          type="button"
          onClick={() => applyKind("Reply")}
          disabled={!canReply}
          className={cn(
            "inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium transition-colors",
            kind === "Reply"
              ? "bg-sky-500/15 text-sky-300 border border-sky-500/30"
              : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]",
            !canReply && "opacity-40 cursor-not-allowed",
          )}
        >
          <Reply className="h-3.5 w-3.5" />
          Reply
        </button>
        <button
          type="button"
          onClick={() => applyKind("ReplyAll")}
          disabled={!canReply}
          className={cn(
            "inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium transition-colors",
            kind === "ReplyAll"
              ? "bg-sky-500/15 text-sky-300 border border-sky-500/30"
              : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]",
            !canReply && "opacity-40 cursor-not-allowed",
          )}
        >
          <ReplyAll className="h-3.5 w-3.5" />
          Reply all
        </button>
        <button
          type="button"
          onClick={() => applyKind("Forward")}
          className={cn(
            "inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium transition-colors",
            kind === "Forward"
              ? "bg-sky-500/15 text-sky-300 border border-sky-500/30"
              : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]",
          )}
        >
          <ForwardIcon className="h-3.5 w-3.5" />
          Forward
        </button>
        <button
          type="button"
          onClick={() => applyKind("New")}
          className={cn(
            "inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium transition-colors",
            kind === "New"
              ? "bg-sky-500/15 text-sky-300 border border-sky-500/30"
              : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]",
          )}
        >
          <Mail className="h-3.5 w-3.5" />
          New
        </button>
      </div>

      <div className="space-y-1.5 text-sm">
        <div className="flex items-center gap-2">
          <label className="w-10 shrink-0 text-xs text-muted-foreground">To</label>
          <input
            type="text"
            value={to}
            onChange={(e) => setTo(e.target.value)}
            placeholder="name@example.com, …"
            className="flex-1 bg-white/[0.03] border border-white/10 rounded-md px-2.5 py-1.5 text-sm focus:outline-none focus:border-white/25"
          />
          {!showCc ? (
            <button
              type="button"
              onClick={() => setShowCc(true)}
              className="text-xs text-muted-foreground hover:text-foreground px-1.5 py-0.5 rounded hover:bg-white/[0.05]"
            >
              Cc
            </button>
          ) : null}
          {!showBcc ? (
            <button
              type="button"
              onClick={() => setShowBcc(true)}
              className="text-xs text-muted-foreground hover:text-foreground px-1.5 py-0.5 rounded hover:bg-white/[0.05]"
            >
              Bcc
            </button>
          ) : null}
        </div>
        {showCc ? (
          <div className="flex items-center gap-2">
            <label className="w-10 shrink-0 text-xs text-muted-foreground">Cc</label>
            <input
              type="text"
              value={cc}
              onChange={(e) => setCc(e.target.value)}
              placeholder="cc@example.com, …"
              className="flex-1 bg-white/[0.03] border border-white/10 rounded-md px-2.5 py-1.5 text-sm focus:outline-none focus:border-white/25"
            />
          </div>
        ) : null}
        {showBcc ? (
          <div className="flex items-center gap-2">
            <label className="w-10 shrink-0 text-xs text-muted-foreground">Bcc</label>
            <input
              type="text"
              value={bcc}
              onChange={(e) => setBcc(e.target.value)}
              placeholder="bcc@example.com, …"
              className="flex-1 bg-white/[0.03] border border-white/10 rounded-md px-2.5 py-1.5 text-sm focus:outline-none focus:border-white/25"
            />
          </div>
        ) : null}
        <div className="flex items-center gap-2">
          <label className="w-10 shrink-0 text-xs text-muted-foreground">
            Subject
          </label>
          <input
            type="text"
            value={subject}
            onChange={(e) => setSubject(e.target.value)}
            placeholder="Subject"
            className="flex-1 bg-white/[0.03] border border-white/10 rounded-md px-2.5 py-1.5 text-sm focus:outline-none focus:border-white/25"
          />
        </div>
      </div>

      <RichTextEditor
        key={editorKey}
        content={initialEditorContent}
        autoFocus={false}
        onChange={(html) => setBodyHtml(html)}
        placeholder="Write your message. Type @@ to tag an agent, :: to attach an intake form..."
        minHeight="140px"
        onUploadFile={attachments.upload}
        onMentionQuery={(q) => userApi.searchAgents(q)}
        onMentionsChange={setMentionedUserIds}
        onIntakeQuery={async (q) => {
          const list = await intakeFormsApi.listTemplates(false);
          const needle = q.trim().toLowerCase();
          const filtered = needle
            ? list.filter(
                (t) =>
                  t.name.toLowerCase().includes(needle) ||
                  (t.description ?? "").toLowerCase().includes(needle),
              )
            : list;
          return filtered.slice(0, 8).map((t) => ({
            id: t.id,
            name: t.name,
            description: t.description,
          }));
        }}
        onIntakeInsert={async (templateId) => {
          try {
            const created = await intakeFormsApi.createDraft(ticketId, {
              templateId,
            });
            return created.instance.id;
          } catch {
            toast.error("Could not attach intake form.");
            return null;
          }
        }}
        onLinkedFormsChange={setLinkedFormIds}
        onIntakeChipClick={(instanceId) => setDrawerInstanceId(instanceId)}
      />

      <IntakePrefillDrawer
        ticketId={ticketId}
        instanceId={drawerInstanceId}
        onClose={() => setDrawerInstanceId(null)}
        onDeleted={(deletedId) => {
          setLinkedFormIds((prev) => prev.filter((id) => id !== deletedId));
        }}
      />

      <AttachmentTray items={attachments.items} onRemove={attachments.remove} />

      <div className="flex items-center justify-between">
        <button
          type="button"
          onClick={onCancel}
          className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-white/[0.06] transition-colors"
        >
          Cancel
        </button>
        <button
          type="submit"
          disabled={mutation.isPending}
          className={cn(
            "px-4 py-2 rounded-md text-sm font-medium transition-colors",
            "bg-sky-500/20 text-sky-300 border border-sky-500/30 hover:bg-sky-500/30",
            mutation.isPending && "opacity-50 cursor-not-allowed",
          )}
        >
          {mutation.isPending ? "Sending..." : "Send mail"}
        </button>
      </div>
    </form>
  );
}
