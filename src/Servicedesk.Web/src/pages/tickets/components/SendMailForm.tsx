import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Reply, ReplyAll, Forward as ForwardIcon, Mail } from "lucide-react";
import {
  ticketApi,
  mentionApi,
  type MailRecipientInput,
  type OutboundMailKind,
} from "@/lib/ticket-api";
import { RichTextEditor, splitMentionIds } from "@/components/RichTextEditor";
import {
  signatureMarkerHtml,
  signatureMarkerBare,
} from "@/components/SignatureBlockNode";
import { cn } from "@/lib/utils";
import type { PendingMailAction } from "@/stores/useWorkspaceStore";
import { AttachmentTray } from "./AttachmentTray";
import { RecipientInput } from "./RecipientInput";
import { useAttachmentUploads } from "../hooks/useAttachmentUploads";
import { intakeFormsApi } from "@/lib/intakeForms-api";
import { composeTemplatesApi } from "@/lib/composeTemplates-api";
import { IntakePrefillDrawer } from "@/components/intake/IntakePrefillDrawer";
import { useAuth } from "@/auth/authStore";
import { orderMentionItems } from "@/pages/orders/orderMention";

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
  /// Ticket queue id — scopes the `::` template picker so an agent only
  /// sees templates configured for the host ticket's queue (plus any
  /// unrestricted templates).
  queueId?: string | null;
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
  // No leading typing paragraphs here — assembleContent() prepends the cursor
  // line(s) (and, when enabled, the signature block) above this quote.
  return `<p>---------- Forwarded message ----------</p>
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
  // No leading typing paragraphs here — assembleContent() prepends the cursor
  // line(s) (and, when enabled, the signature block) above this quote.
  return `<p>${preamble}</p>
<blockquote>${body}</blockquote>`;
}

/// Strip the (empty) signature marker so emptiness checks ignore a pre-loaded
/// signature — an agent who only sees their signature still hasn't written a
/// message.
function stripSignatureMarker(html: string): string {
  return html.replace(/<div[^>]*\bdata-sd-signature\b[^>]*>\s*<\/div>/gi, "");
}

/// Assemble the editor content from a quote (possibly empty) and the resolved
/// signature preview. Returns two strings: `rich` carries the full data-URI
/// preview inside the marker (what the editor renders) while `bare` carries the
/// bare marker (what we send + validate against — the editor serialises the
/// SignatureBlock to exactly this on send). Order is always
/// [cursor line(s)] → [signature] → [quote].
function assembleContent(
  quote: string,
  signatureHtml: string | null,
): { rich: string; bare: string } {
  const typing = quote ? "<p></p><p></p>" : "<p></p>";
  const richMarker = signatureHtml ? signatureMarkerHtml(signatureHtml) : "";
  const bareMarker = signatureHtml ? signatureMarkerBare() : "";
  return {
    rich: typing + richMarker + quote,
    bare: typing + bareMarker + quote,
  };
}

export function SendMailForm({ ticketId, queueId, context, initialIntent, onSent, onCancel }: Props) {
  const { user } = useAuth();
  const [kind, setKind] = React.useState<OutboundMailKind>(
    initialIntent?.kind ?? (context.latestInbound ? "Reply" : "New"),
  );
  const [to, setTo] = React.useState<MailRecipientInput[]>([]);
  const [cc, setCc] = React.useState<MailRecipientInput[]>([]);
  const [bcc, setBcc] = React.useState<MailRecipientInput[]>([]);
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

  // Tokens for the :: template picker. Same cache key/staleTime as the
  // note/reply form so both tabs share the resolved values.
  const tokensQ = useQuery({
    queryKey: ["compose-templates", "resolve", { ticketId }],
    queryFn: () => composeTemplatesApi.resolveTokens({ ticketId }),
    staleTime: 60_000,
  });
  const composeTokens = tokensQ.data?.tokens;
  const queryClient = useQueryClient();
  const attachments = useAttachmentUploads(ticketId);

  // v0.0.61 — resolved signature preview to pre-load as a fixed block above the
  // quote. Keyed by reply-ness so the server's reply gating (AppendOnReplies)
  // is honoured; null when nothing should be pre-loaded.
  const replyish = kind !== "New";
  const signatureQ = useQuery({
    queryKey: ["compose-signature", ticketId, replyish],
    queryFn: () => ticketApi.composeSignature(ticketId, replyish),
    staleTime: 60_000,
  });
  const signatureHtml = signatureQ.data?.html ?? null;
  // Refs let applyKind (memoised on [context]) read the freshest values without
  // re-subscribing, and let the re-apply effect tell "agent hasn't typed yet".
  const signatureHtmlRef = React.useRef<string | null>(signatureHtml);
  const lastQuoteRef = React.useRef<string | null>(null);
  const lastSigRef = React.useRef<string | null>(null);
  const lastAppliedBareRef = React.useRef<string | null>(null);
  React.useEffect(() => {
    signatureHtmlRef.current = signatureHtml;
  }, [signatureHtml]);

  const applyKind = React.useCallback(
    (
      nextKind: OutboundMailKind,
      override?: PendingMailAction["source"] | null,
    ) => {
      setKind(nextKind);
      // Assemble [cursor] → [signature block] → [quote] from the freshest
      // resolved signature, and record what we applied so the re-apply effect
      // can fold the signature in once it loads (while the agent is pristine).
      const applyContent = (quote: string) => {
        const sig = signatureHtmlRef.current;
        const { rich, bare } = assembleContent(quote, sig);
        setInitialEditorContent(rich || undefined);
        setBodyHtml(bare);
        lastQuoteRef.current = quote;
        lastSigRef.current = sig;
        lastAppliedBareRef.current = bare;
        setEditorKey((k) => k + 1);
      };
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
        setTo(context.requesterEmail ? [{ address: context.requesterEmail }] : []);
        setCc([]);
        setBcc([]);
        setSubject(ensureTicketTag(context.ticketSubject, context.ticketNumber));
        applyContent("");
        return;
      }

      if (nextKind === "Forward") {
        setTo([]);
        setCc([]);
        setBcc([]);
        const baseSubject = source?.subject ?? context.ticketSubject;
        setSubject(ensureTicketTag(ensureFwdPrefix(baseSubject), context.ticketNumber));
        applyContent(source ? buildForwardQuote(source) : "");
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

      setTo(dedupe(replyTargets));
      if (nextKind === "ReplyAll") {
        const ccList = dedupe(replyAllCc);
        setCc(ccList);
        if (ccList.length > 0) setShowCc(true);
      } else {
        setCc([]);
      }
      setBcc([]);
      setSubject(
        ensureTicketTag(
          ensureRePrefix(source.subject ?? context.ticketSubject),
          context.ticketNumber,
        ),
      );
      applyContent(buildReplyQuote(source));
    },
    [context],
  );

  // Fold the signature in (or out) once the preview resolves — but only while
  // the agent hasn't touched the editor yet, so a late-arriving signature never
  // clobbers a message in progress. Compares the live body to the exact string
  // we last applied; any edit (or a different signature) breaks the match.
  React.useEffect(() => {
    if (lastQuoteRef.current === null) return; // nothing applied yet
    if (signatureHtml === lastSigRef.current) return; // no change to fold
    if (bodyHtml !== lastAppliedBareRef.current) return; // agent has edited
    const { rich, bare } = assembleContent(lastQuoteRef.current, signatureHtml);
    setInitialEditorContent(rich || undefined);
    setBodyHtml(bare);
    lastSigRef.current = signatureHtml;
    lastAppliedBareRef.current = bare;
    setEditorKey((k) => k + 1);
  }, [signatureHtml, bodyHtml]);

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
    mutationFn: () => {
      const { userIds, mailboxIds } = splitMentionIds(mentionedUserIds);
      return ticketApi.sendMail(ticketId, {
        kind,
        to,
        cc,
        bcc,
        subject: subject.trim(),
        bodyHtml,
        attachmentIds: attachments.readyAttachmentIds,
        mentionedUserIds: userIds.length > 0 ? userIds : undefined,
        mentionedMailboxIds: mailboxIds.length > 0 ? mailboxIds : undefined,
        linkedFormIds: linkedFormIds.length > 0 ? linkedFormIds : undefined,
        // True when a signature was pre-loaded for this composition: the server
        // then swaps the body's marker for the real signature (or, if the agent
        // removed the block, adds nothing) instead of appending at the bottom.
        signaturePreloaded: lastSigRef.current != null,
      });
    },
    onSuccess: () => {
      toast.success("Mail sent");
      // Clear the applied-content markers so the re-apply effect doesn't
      // re-inject a signature into the now-empty editor.
      lastQuoteRef.current = null;
      lastSigRef.current = null;
      lastAppliedBareRef.current = null;
      setBodyHtml("");
      setMentionedUserIds([]);
      setLinkedFormIds([]);
      attachments.reset();
      setEditorKey((k) => k + 1);
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      queryClient.invalidateQueries({ queryKey: ["sla", "ticket", ticketId] });
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
    if (to.length === 0 && cc.length === 0 && bcc.length === 0) {
      toast.error("At least one recipient is required");
      return;
    }
    if (!subject.trim()) {
      toast.error("Subject is required");
      return;
    }
    const bodyForCheck = stripSignatureMarker(bodyHtml).trim();
    if (!bodyForCheck || bodyForCheck === "<p></p>") {
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
              : "text-muted-foreground hover:text-foreground hover:bg-glass-hover",
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
              : "text-muted-foreground hover:text-foreground hover:bg-glass-hover",
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
              : "text-muted-foreground hover:text-foreground hover:bg-glass-hover",
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
              : "text-muted-foreground hover:text-foreground hover:bg-glass-hover",
          )}
        >
          <Mail className="h-3.5 w-3.5" />
          New
        </button>
      </div>

      <div className="space-y-1.5 text-sm">
        <div className="flex items-center gap-2">
          <label className="w-10 shrink-0 text-xs text-muted-foreground">To</label>
          <RecipientInput
            value={to}
            onChange={setTo}
            ariaLabel="To recipients"
            placeholder="name@example.com, …"
          />
          {!showCc ? (
            <button
              type="button"
              onClick={() => setShowCc(true)}
              className="text-xs text-muted-foreground hover:text-foreground px-1.5 py-0.5 rounded hover:bg-glass-hover"
            >
              Cc
            </button>
          ) : null}
          {!showBcc ? (
            <button
              type="button"
              onClick={() => setShowBcc(true)}
              className="text-xs text-muted-foreground hover:text-foreground px-1.5 py-0.5 rounded hover:bg-glass-hover"
            >
              Bcc
            </button>
          ) : null}
        </div>
        {showCc ? (
          <div className="flex items-center gap-2">
            <label className="w-10 shrink-0 text-xs text-muted-foreground">Cc</label>
            <RecipientInput
              value={cc}
              onChange={setCc}
              ariaLabel="Cc recipients"
              placeholder="cc@example.com, …"
            />
          </div>
        ) : null}
        {showBcc ? (
          <div className="flex items-center gap-2">
            <label className="w-10 shrink-0 text-xs text-muted-foreground">Bcc</label>
            <RecipientInput
              value={bcc}
              onChange={setBcc}
              ariaLabel="Bcc recipients"
              placeholder="bcc@example.com, …"
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
            className="flex-1 bg-glass border border-glass rounded-md px-2.5 py-1.5 text-sm focus:outline-none focus:border-glass-strong"
          />
        </div>
      </div>

      <RichTextEditor
        key={editorKey}
        content={initialEditorContent}
        enableSignatureBlock
        autoFocus={false}
        onChange={(html) => setBodyHtml(html)}
        placeholder="Write your message. Type @@ to tag an agent, :: to insert a template or intake form..."
        minHeight="140px"
        onUploadFile={attachments.upload}
        onMentionQuery={(q) => mentionApi.search(q)}
        onMentionsChange={setMentionedUserIds}
        composeTokens={composeTokens}
        onIntakeQuery={async (q) => {
          // Merge two sources behind a single `::` picker:
          //   - compose templates → insert HTML body inline
          //   - intake-form templates → insert chip + async draft create
          // Settled-promise so a failing fetch on one side doesn't blank
          // the other. Compose templates appear first; the picker shows a
          // kind badge so the difference is obvious.
          const [composedRes, intakeRes] = await Promise.allSettled([
            composeTemplatesApi.usable(queueId ?? null),
            intakeFormsApi.listTemplates(false),
          ]);
          const needle = q.trim().toLowerCase();
          const matchesNeedle = (name: string, description: string | null | undefined) =>
            !needle ||
            name.toLowerCase().includes(needle) ||
            (description ?? "").toLowerCase().includes(needle);

          const templateItems = composedRes.status === "fulfilled"
            ? composedRes.value
                .filter((t) => matchesNeedle(t.name, t.description))
                .map((t) => ({
                  id: t.id,
                  name: t.name,
                  description: t.description,
                  kind: "template" as const,
                  bodyHtml: t.bodyHtml,
                }))
            : [];

          const intakeItems = intakeRes.status === "fulfilled"
            ? intakeRes.value
                .filter((t) => matchesNeedle(t.name, t.description))
                .map((t) => ({
                  id: t.id,
                  name: t.name,
                  description: t.description,
                  kind: "intake" as const,
                }))
            : [];

          // v0.0.59 — also offer Adsolut orders for users with the Orders flag.
          const orderItems = user?.adsolutOrdersEnabled ? await orderMentionItems(q) : [];
          return [...templateItems, ...intakeItems, ...orderItems].slice(0, 16);
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
          className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-glass-hover transition-colors"
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
