import * as React from "react";
import { Reply, ReplyAll, Forward as ForwardIcon, GitBranch, Download, FileX } from "lucide-react";
import { formatBytes } from "@/lib/format";
import type { TicketEvent, OutboundMailKind } from "@/lib/ticket-api";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { SplitMailDialog } from "@/components/SplitMailDialog";
import { CollapsibleBody } from "./CollapsibleBody";
import { AttachmentChip, PostAttachmentStrip } from "./attachments";
import { readAttachmentsFromMeta } from "./attachmentMeta";

type Meta = Record<string, unknown>;

function MailActionButton({
  icon: Icon,
  label,
  onClick,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs text-muted-foreground hover:text-foreground hover:bg-glass-hover transition-colors"
    >
      <Icon className="h-3 w-3" />
      {label}
    </button>
  );
}

export function MailSentBody({
  event,
  meta,
  readOnly,
}: {
  event: TicketEvent;
  meta: Meta;
  readOnly: boolean;
}) {
  const fromAddr = typeof meta.from === "string" ? meta.from : null;
  const fromName =
    typeof meta.fromName === "string" && meta.fromName.length > 0
      ? meta.fromName
      : null;
  const subject = typeof meta.subject === "string" ? meta.subject : null;
  const sentAttachments = readAttachmentsFromMeta(meta);
  const toList = Array.isArray(meta.to)
    ? (meta.to as Array<{ address: string; name?: string }>)
    : [];
  const ccList = Array.isArray(meta.cc)
    ? (meta.cc as Array<{ address: string; name?: string }>)
    : [];
  const normalizedTo = toList
    .filter((r) => typeof r?.address === "string" && r.address.length > 0)
    .map((r) => ({ address: r.address, name: r.name ?? r.address }));
  const normalizedCc = ccList
    .filter((r) => typeof r?.address === "string" && r.address.length > 0)
    .map((r) => ({ address: r.address, name: r.name ?? r.address }));

  const startOutboundAction = (kind: OutboundMailKind) => {
    useWorkspaceStore.getState().requestMailAction({
      ticketId: event.ticketId,
      kind,
      source: {
        from: fromAddr
          ? { address: fromAddr, name: fromName ?? fromAddr }
          : null,
        to: normalizedTo,
        cc: normalizedCc,
        subject,
        bodyHtml: event.bodyHtml ?? null,
        receivedUtc: event.createdUtc ?? null,
        isOutbound: true,
      },
    });
  };

  return (
    <div className="space-y-2">
      <CollapsibleBody html={event.bodyHtml} text={event.bodyText} />
      <PostAttachmentStrip attachments={sentAttachments} />
      {!readOnly && (
        <div className="flex flex-wrap items-center gap-1 pt-1">
          <MailActionButton icon={Reply} label="Reply" onClick={() => startOutboundAction("Reply")} />
          {normalizedCc.length > 0 ? (
            <MailActionButton icon={ReplyAll} label="Reply all" onClick={() => startOutboundAction("ReplyAll")} />
          ) : null}
          <MailActionButton icon={ForwardIcon} label="Forward" onClick={() => startOutboundAction("Forward")} />
        </div>
      )}
    </div>
  );
}

export function MailReceivedBody({
  event,
  meta,
  readOnly,
}: {
  event: TicketEvent;
  meta: Meta;
  readOnly: boolean;
}) {
  const [splitOpen, setSplitOpen] = React.useState(false);
  const fromAddrRaw = typeof meta.from === "string" ? meta.from : null;
  const fromNameRaw =
    typeof meta.fromName === "string" && meta.fromName.length > 0
      ? meta.fromName
      : null;
  const subject =
    typeof meta.subject === "string" ? meta.subject : null;
  const mailId =
    typeof meta.mail_message_id === "string"
      ? meta.mail_message_id
      : null;
  const attachments = Array.isArray(meta.attachments)
    ? (meta.attachments as Array<{
        id: string;
        name: string;
        mimeType: string;
        size: number;
        url: string;
      }>)
    : [];
  const skippedAttachments = Array.isArray(meta.skippedAttachments)
    ? (meta.skippedAttachments as Array<{ name?: unknown; size?: unknown }>)
        .filter((a) => typeof a?.name === "string")
        .map((a) => ({ name: a.name as string, size: typeof a.size === "number" ? a.size : 0 }))
    : [];
  const metaTo = Array.isArray(meta.to)
    ? (meta.to as Array<{ address: string; name?: string }>)
        .filter((r) => typeof r?.address === "string" && r.address.length > 0)
        .map((r) => ({ address: r.address, name: r.name ?? r.address }))
    : [];
  const metaCc = Array.isArray(meta.cc)
    ? (meta.cc as Array<{ address: string; name?: string }>)
        .filter((r) => typeof r?.address === "string" && r.address.length > 0)
        .map((r) => ({ address: r.address, name: r.name ?? r.address }))
    : [];

  const startMailAction = (kind: OutboundMailKind) => {
    useWorkspaceStore.getState().requestMailAction({
      ticketId: event.ticketId,
      kind,
      source: {
        from: fromAddrRaw
          ? { address: fromAddrRaw, name: fromNameRaw ?? fromAddrRaw }
          : null,
        to: metaTo,
        cc: metaCc,
        subject,
        bodyHtml: event.bodyHtml ?? null,
        receivedUtc: event.createdUtc ?? null,
        isOutbound: false,
      },
    });
  };

  return (
    <div className="space-y-2">
      <CollapsibleBody html={event.bodyHtml} text={event.bodyText} />
      {attachments.length > 0 ? (
        <div className="flex flex-wrap gap-2 pt-1">
          {attachments.map((a) => (
            <AttachmentChip key={a.id} attachment={a} />
          ))}
        </div>
      ) : null}
      {skippedAttachments.length > 0 ? (
        <div className="flex flex-wrap gap-2">
          {skippedAttachments.map((a, i) => (
            <span
              key={`${a.name}-${i}`}
              className="inline-flex items-center gap-2 rounded-md border border-dashed border-border/60 px-2.5 py-1.5 text-xs text-muted-foreground"
              title="Not downloaded: larger than the inbound attachment limit. The original mail in the mailbox still has it."
            >
              <FileX className="h-3.5 w-3.5" />
              <span className="max-w-[220px] truncate">{a.name}</span>
              <span>{formatBytes(a.size)} · too large</span>
            </span>
          ))}
        </div>
      ) : null}
      {!readOnly && (
        <div className="flex flex-wrap items-center gap-1 pt-1">
          <MailActionButton icon={Reply} label="Reply" onClick={() => startMailAction("Reply")} />
          <MailActionButton icon={ReplyAll} label="Reply all" onClick={() => startMailAction("ReplyAll")} />
          <MailActionButton icon={ForwardIcon} label="Forward" onClick={() => startMailAction("Forward")} />
          <MailActionButton icon={GitBranch} label="Split" onClick={() => setSplitOpen(true)} />
          {mailId ? (
            <a
              href={`/api/tickets/${event.ticketId}/mail/${mailId}/raw`}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs text-muted-foreground hover:text-foreground hover:bg-glass-hover transition-colors ml-auto"
            >
              <Download className="h-3 w-3" />
              .eml
            </a>
          ) : null}
        </div>
      )}
      {!readOnly && (
        <SplitMailDialog
          open={splitOpen}
          sourceTicketId={event.ticketId}
          sourceMailEventId={event.id}
          mailSubject={subject}
          onClose={() => setSplitOpen(false)}
        />
      )}
    </div>
  );
}
