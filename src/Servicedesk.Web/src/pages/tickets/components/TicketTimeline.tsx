import * as React from "react";
import DOMPurify from "dompurify";
import { useServerTime, toServerLocal, formatUtcSuffix } from "@/hooks/useServerTime";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  MessageSquarePlus,
  MessageCircle,
  StickyNote,
  ArrowRightCircle,
  UserPlus,
  Flag,
  Inbox,
  Tag,
  Info,
  Mail,
  MailOpen,
  Send,
  Reply,
  ReplyAll,
  Forward as ForwardIcon,
  Building2,
  Download,
  Pencil,
  Pin,
  UserCog,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { ticketApi, type TicketEvent, type OutboundMailKind } from "@/lib/ticket-api";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { RichTextEditor } from "@/components/RichTextEditor";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  AttachmentPreviewDialog,
  canPreview,
  type AttachmentPreview,
} from "@/components/attachments/AttachmentPreviewDialog";
import { EventRevisionDialog } from "./EventRevisionDialog";

type PreviewContextValue = {
  open: (preview: AttachmentPreview) => void;
};
const PreviewContext = React.createContext<PreviewContextValue | null>(null);
function usePreview() {
  return React.useContext(PreviewContext);
}

function inlineUrl(url: string): string {
  return url.includes("?") ? `${url}&inline=true` : `${url}?inline=true`;
}

type TicketTimelineProps = {
  ticketId: string;
  events: TicketEvent[];
  pinnedEventIds: Set<number>;
};

function formatBytes(n: number): string {
  if (!Number.isFinite(n) || n < 0) return "";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

/// DOMPurify config that preserves @@-mention chips. Tiptap's Mention
/// extension emits `<span data-type="mention" class="sd-mention"
/// data-id="..." data-label="...">@john</span>`. The default sanitiser
/// strips `data-*` attributes, which would flatten the chip to plain
/// text and (more importantly) destroy the agent-id needed for any
/// future hover / click-through affordance. We allow the three data
/// attrs *only* on `span` elements and do not enable HTML5-custom-data
/// on anything else (tightest scope that still lets the chip survive).
const SANITIZE_CONFIG = {
  ADD_ATTR: ["data-type", "data-id", "data-label"],
};

function SafeHtml({ html }: { html: string }) {
  const preview = usePreview();
  const onClick = React.useCallback(
    (e: React.MouseEvent<HTMLDivElement>) => {
      if (!preview) return;
      const target = e.target as HTMLElement;
      if (target.tagName !== "IMG") return;
      const img = target as HTMLImageElement;
      const src = img.currentSrc || img.src;
      if (!src) return;
      e.preventDefault();
      // Inline images served by the mail-attachment endpoint will already
      // accept the `inline=true` hint; external images pass through untouched.
      const url = src.includes("/api/tickets/") ? inlineUrl(src) : src;
      preview.open({
        url,
        mimeType: "image/*",
        filename: img.alt || "Image",
      });
    },
    [preview],
  );
  // Memoise the sanitised output per html-string. Without this, sanitize()
  // runs on every render; dompurify 3.4.x can return a string whose byte
  // content differs between calls on the same input, which tricks React's
  // dangerouslySetInnerHTML diff into swapping the DOM, which re-creates
  // every inline <img>, which re-fires its GET — the classic "200 reqs
  // for the same 2 attachments in one second" loop.
  const sanitized = React.useMemo(
    () => DOMPurify.sanitize(html, SANITIZE_CONFIG) as unknown as string,
    [html],
  );
  return (
    <div
      onClick={onClick}
      className="prose-sm text-foreground/90 [&_a]:text-primary [&_a]:underline [&_p]:my-1 [&_ul]:pl-5 [&_ol]:pl-5 [&_img]:cursor-zoom-in"
      dangerouslySetInnerHTML={{ __html: sanitized }}
    />
  );
}

/// Renders a post body (HTML preferred, plain text fallback) with a
/// collapse affordance: caps visible height at COLLAPSED_MAX_PX, fades
/// the bottom edge into the background, and shows a Read more / Show
/// less button. Threshold includes a small slack so a body that just
/// barely exceeds the cap doesn't get a needless toggle. Re-measures
/// on body-change and when inline images finish loading (they can
/// significantly bump the rendered height after first paint).
function CollapsibleBody({
  html,
  text,
}: {
  html: string | null | undefined;
  text: string | null | undefined;
}) {
  const COLLAPSED_MAX_PX = 360;
  const SLACK_PX = 16;
  const ref = React.useRef<HTMLDivElement>(null);
  const [expanded, setExpanded] = React.useState(false);
  const [overflows, setOverflows] = React.useState(false);

  React.useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      setOverflows(el.scrollHeight > COLLAPSED_MAX_PX + SLACK_PX);
    };
    measure();
    // Re-measure once each image settles; scrollHeight before img.load
    // is 0-for-that-image, so the cap misfires on image-heavy bodies
    // without this.
    const imgs = Array.from(el.querySelectorAll("img"));
    const onImg = () => measure();
    imgs.forEach((img) => {
      if (!img.complete) {
        img.addEventListener("load", onImg);
        img.addEventListener("error", onImg);
      }
    });
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => {
      ro.disconnect();
      imgs.forEach((img) => {
        img.removeEventListener("load", onImg);
        img.removeEventListener("error", onImg);
      });
    };
  }, [html, text]);

  const hasBody = (html && html.length > 0) || (text && text.length > 0);
  if (!hasBody) return null;

  const collapsed = overflows && !expanded;
  return (
    <div>
      <div
        className={cn("relative", collapsed && "overflow-hidden")}
        style={collapsed ? { maxHeight: COLLAPSED_MAX_PX } : undefined}
      >
        <div ref={ref}>
          {html ? (
            <SafeHtml html={html} />
          ) : (
            <p className="whitespace-pre-wrap text-sm text-foreground/90">{text}</p>
          )}
        </div>
        {collapsed ? (
          <div className="pointer-events-none absolute inset-x-0 bottom-0 h-16 bg-gradient-to-t from-background via-background/80 to-transparent" />
        ) : null}
      </div>
      {overflows ? (
        <button
          type="button"
          onClick={() => setExpanded((e) => !e)}
          className="mt-1 inline-flex items-center gap-1 text-xs font-medium text-primary hover:text-primary/80"
        >
          {expanded ? "Show less" : "Read more…"}
        </button>
      ) : null}
    </div>
  );
}

type MailAttachment = {
  id: string;
  name: string;
  mimeType: string;
  size: number;
  url: string;
};

function AttachmentChip({ attachment: a }: { attachment: MailAttachment }) {
  const preview = usePreview();
  const canPreviewFile = canPreview(a.mimeType);
  const handleClick = (e: React.MouseEvent) => {
    if (!canPreviewFile || !preview) return;
    e.preventDefault();
    preview.open({
      url: inlineUrl(a.url),
      mimeType: a.mimeType,
      filename: a.name,
      sizeLabel: formatBytes(a.size),
      downloadUrl: a.url,
    });
  };
  return (
    <a
      href={a.url}
      target="_blank"
      rel="noreferrer"
      onClick={handleClick}
      className="inline-flex items-center gap-2 rounded-md border border-border/60 bg-background/40 px-2.5 py-1.5 text-xs text-foreground/90 hover:border-primary/50 hover:bg-primary/10"
      title={
        canPreviewFile
          ? `Preview · ${a.mimeType} · ${formatBytes(a.size)}`
          : `${a.mimeType} · ${formatBytes(a.size)}`
      }
    >
      <Download className="h-3.5 w-3.5 text-primary" />
      <span className="max-w-[220px] truncate">{a.name}</span>
      <span className="text-muted-foreground">{formatBytes(a.size)}</span>
    </a>
  );
}


type EventConfig = {
  icon: React.ComponentType<{ className?: string }>;
  dotColor: string;
  label: string;
};

const EVENT_CONFIG: Record<string, EventConfig> = {
  Created: {
    icon: MessageSquarePlus,
    dotColor: "bg-purple-500",
    label: "Ticket created",
  },
  Comment: {
    icon: MessageCircle,
    dotColor: "bg-amber-500",
    label: "Reply",
  },
  Mail: {
    icon: Mail,
    dotColor: "bg-sky-500",
    label: "Email",
  },
  MailReceived: {
    icon: MailOpen,
    dotColor: "bg-sky-400",
    label: "Mail received",
  },
  MailSent: {
    icon: Send,
    dotColor: "bg-sky-500",
    label: "Mail sent",
  },
  Note: {
    icon: StickyNote,
    dotColor: "bg-blue-500",
    label: "Internal note",
  },
  StatusChange: {
    icon: ArrowRightCircle,
    dotColor: "bg-green-500",
    label: "Status changed",
  },
  AssignmentChange: {
    icon: UserPlus,
    dotColor: "bg-indigo-400",
    label: "Assignment changed",
  },
  PriorityChange: {
    icon: Flag,
    dotColor: "bg-red-400",
    label: "Priority changed",
  },
  QueueChange: {
    icon: Inbox,
    dotColor: "bg-cyan-500",
    label: "Queue changed",
  },
  CategoryChange: {
    icon: Tag,
    dotColor: "bg-teal-500",
    label: "Category changed",
  },
  CompanyAssignment: {
    icon: Building2,
    dotColor: "bg-purple-400",
    label: "Company assigned",
  },
  RequesterChange: {
    icon: UserCog,
    dotColor: "bg-fuchsia-400",
    label: "Requester changed",
  },
  SystemNote: {
    icon: Info,
    dotColor: "bg-white/30",
    label: "System",
  },
};

function parseMetadata(json: string): Record<string, unknown> {
  try {
    return JSON.parse(json) as Record<string, unknown>;
  } catch {
    return {};
  }
}

function MetaChangeText({
  meta,
  fieldLabel,
}: {
  meta: Record<string, unknown>;
  fieldLabel: string;
}) {
  const from = meta.fromName ?? meta.from ?? meta.fromId;
  const to = meta.toName ?? meta.to ?? meta.toId;
  if (from && to) {
    return (
      <span className="text-sm text-muted-foreground">
        {fieldLabel} changed from{" "}
        <span className="text-foreground/80">{String(from)}</span> to{" "}
        <span className="text-foreground/80">{String(to)}</span>
      </span>
    );
  }
  if (to) {
    return (
      <span className="text-sm text-muted-foreground">
        {fieldLabel} set to{" "}
        <span className="text-foreground/80">{String(to)}</span>
      </span>
    );
  }
  return (
    <span className="text-sm text-muted-foreground">{fieldLabel} changed</span>
  );
}

function AssignmentText({ meta }: { meta: Record<string, unknown> }) {
  const toName = meta.toName ?? meta.toEmail ?? meta.toUserId;
  if (!toName) {
    return (
      <span className="text-sm text-muted-foreground">Ticket unassigned</span>
    );
  }
  return (
    <span className="text-sm text-muted-foreground">
      Assigned to <span className="text-foreground/80">{String(toName)}</span>
    </span>
  );
}

function readAttachmentsFromMeta(meta: Record<string, unknown>): MailAttachment[] {
  if (!Array.isArray(meta.attachments)) return [];
  return (meta.attachments as Array<Partial<MailAttachment>>)
    .filter(
      (a): a is MailAttachment =>
        typeof a?.id === "string" &&
        typeof a?.name === "string" &&
        typeof a?.url === "string",
    )
    .map((a) => ({
      id: a.id,
      name: a.name,
      mimeType: typeof a.mimeType === "string" ? a.mimeType : "application/octet-stream",
      size: typeof a.size === "number" ? a.size : 0,
      url: a.url,
    }));
}

function PostAttachmentStrip({ attachments }: { attachments: MailAttachment[] }) {
  if (attachments.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-2 pt-1">
      {attachments.map((a) => (
        <AttachmentChip key={a.id} attachment={a} />
      ))}
    </div>
  );
}

function EventBody({ event }: { event: TicketEvent }) {
  const meta = parseMetadata(event.metadataJson);

  switch (event.eventType) {
    case "Created": {
      const source = typeof meta.source === "string" ? meta.source : null;
      const via =
        source && source.toLowerCase() !== "web"
          ? ` via ${source.toLowerCase()}`
          : "";
      return (
        <span className="text-sm text-muted-foreground">
          Ticket created{via}
        </span>
      );
    }

    case "StatusChange":
      return <MetaChangeText meta={meta} fieldLabel="Status" />;

    case "AssignmentChange":
      return <AssignmentText meta={meta} />;

    case "PriorityChange":
      return <MetaChangeText meta={meta} fieldLabel="Priority" />;

    case "QueueChange":
      return <MetaChangeText meta={meta} fieldLabel="Queue" />;

    case "CategoryChange":
      return <MetaChangeText meta={meta} fieldLabel="Category" />;

    case "CompanyAssignment":
      return <MetaChangeText meta={meta} fieldLabel="Company" />;

    case "RequesterChange": {
      const fromName =
        (typeof meta.fromName === "string" && meta.fromName.trim().length > 0)
          ? meta.fromName
          : (typeof meta.fromEmail === "string" ? meta.fromEmail : null);
      const toName =
        (typeof meta.toName === "string" && meta.toName.trim().length > 0)
          ? meta.toName
          : (typeof meta.toEmail === "string" ? meta.toEmail : null);
      const fromCompany = typeof meta.fromCompanyName === "string" ? meta.fromCompanyName : null;
      const toCompany = typeof meta.toCompanyName === "string" ? meta.toCompanyName : null;
      return (
        <span className="text-sm text-muted-foreground">
          Requester changed from{" "}
          <span className="text-foreground/80">{fromName ?? "unknown"}</span>
          {fromCompany && (
            <span className="text-muted-foreground/70"> ({fromCompany})</span>
          )}
          {" "}to{" "}
          <span className="text-foreground/80">{toName ?? "unknown"}</span>
          {toCompany && (
            <span className="text-muted-foreground/70"> ({toCompany})</span>
          )}
        </span>
      );
    }

    case "SystemNote":
      return (
        <span className="text-sm italic text-muted-foreground">
          {event.bodyText ?? "System event"}
        </span>
      );

    case "MailSent": {
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
      const formatList = (list: Array<{ address: string; name?: string }>) =>
        list
          .map((r) => (r.name && r.name !== r.address ? `${r.name} <${r.address}>` : r.address))
          .join(", ");

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
          <div className="text-xs text-muted-foreground space-y-0.5">
            {fromAddr ? (
              <div>
                From <span className="text-foreground/80">{fromAddr}</span>
              </div>
            ) : null}
            {toList.length > 0 ? (
              <div>
                To <span className="text-foreground/80">{formatList(toList)}</span>
              </div>
            ) : null}
            {ccList.length > 0 ? (
              <div>
                Cc <span className="text-foreground/80">{formatList(ccList)}</span>
              </div>
            ) : null}
            {subject ? (
              <div>
                <span className="text-foreground/80">{subject}</span>
              </div>
            ) : null}
          </div>
          <CollapsibleBody html={event.bodyHtml} text={event.bodyText} />
          <PostAttachmentStrip attachments={sentAttachments} />
          <div className="flex flex-wrap items-center gap-1 pt-1">
            <button
              type="button"
              onClick={() => startOutboundAction("Reply")}
              className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors"
            >
              <Reply className="h-3 w-3" />
              Reply
            </button>
            {normalizedCc.length > 0 ? (
              <button
                type="button"
                onClick={() => startOutboundAction("ReplyAll")}
                className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors"
              >
                <ReplyAll className="h-3 w-3" />
                Reply all
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => startOutboundAction("Forward")}
              className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors"
            >
              <ForwardIcon className="h-3 w-3" />
              Forward
            </button>
          </div>
        </div>
      );
    }

    case "MailReceived": {
      const fromAddrRaw = typeof meta.from === "string" ? meta.from : null;
      const fromNameRaw =
        typeof meta.fromName === "string" && meta.fromName.length > 0
          ? meta.fromName
          : null;
      const fromDisplay = fromNameRaw ?? fromAddrRaw;
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
          <div className="text-xs text-muted-foreground">
            {fromDisplay ? (
              <>
                From <span className="text-foreground/80">{fromDisplay}</span>
              </>
            ) : null}
            {subject ? (
              <>
                {fromDisplay ? " · " : ""}
                <span className="text-foreground/80">{subject}</span>
              </>
            ) : null}
          </div>
          <CollapsibleBody html={event.bodyHtml} text={event.bodyText} />
          {attachments.length > 0 ? (
            <div className="flex flex-wrap gap-2 pt-1">
              {attachments.map((a) => (
                <AttachmentChip key={a.id} attachment={a} />
              ))}
            </div>
          ) : null}
          <div className="flex flex-wrap items-center gap-1 pt-1">
            <button
              type="button"
              onClick={() => startMailAction("Reply")}
              className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors"
            >
              <Reply className="h-3 w-3" />
              Reply
            </button>
            <button
              type="button"
              onClick={() => startMailAction("ReplyAll")}
              className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors"
            >
              <ReplyAll className="h-3 w-3" />
              Reply all
            </button>
            <button
              type="button"
              onClick={() => startMailAction("Forward")}
              className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors"
            >
              <ForwardIcon className="h-3 w-3" />
              Forward
            </button>
            {mailId ? (
              <a
                href={`/api/tickets/${event.ticketId}/mail/${mailId}/raw`}
                target="_blank"
                rel="noreferrer"
                className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors ml-auto"
              >
                <Download className="h-3 w-3" />
                .eml
              </a>
            ) : null}
          </div>
        </div>
      );
    }

    default: {
      const postAttachments = readAttachmentsFromMeta(meta);
      const hasBody = !!(event.bodyHtml || event.bodyText);
      if (!hasBody && postAttachments.length === 0) return null;
      return (
        <div className="space-y-2">
          <CollapsibleBody html={event.bodyHtml} text={event.bodyText} />
          <PostAttachmentStrip attachments={postAttachments} />
        </div>
      );
    }
  }
}

/* ─── Editable event card ─── */

const EDITABLE_TYPES = new Set(["Comment", "Note", "Mail"]);
const PINNABLE_TYPES = new Set(["Comment", "Note", "Mail", "MailReceived", "MailSent"]);

function TimelineEvent({
  event,
  ticketId,
  isPinned,
}: {
  event: TicketEvent;
  ticketId: string;
  isPinned: boolean;
}) {
  const queryClient = useQueryClient();
  const { time: serverTime } = useServerTime();
  const offset = serverTime?.offsetMinutes ?? 0;
  const fmtDate = (iso: string) => toServerLocal(iso, offset);
  const fmtUtc = (iso: string) => formatUtcSuffix(iso);
  const [editing, setEditing] = React.useState(false);
  const [draftHtml, setDraftHtml] = React.useState(
    event.bodyHtml ?? event.bodyText ?? ""
  );
  const [draftInternal, setDraftInternal] = React.useState(event.isInternal);
  const [editorKey, setEditorKey] = React.useState(0);
  const [revisionOpen, setRevisionOpen] = React.useState(false);
  const [pinDialogOpen, setPinDialogOpen] = React.useState(false);
  const [pinRemark, setPinRemark] = React.useState("");

  const config = EVENT_CONFIG[event.eventType] ?? {
    icon: Info,
    dotColor: "bg-white/30",
    label: event.eventType,
  };
  const Icon = config.icon;
  const isEditable = EDITABLE_TYPES.has(event.eventType);
  const isPinnable = PINNABLE_TYPES.has(event.eventType);
  const isPublicComment = !event.isInternal && event.eventType === "Comment";
  const isSystemLike =
    event.eventType === "SystemNote" ||
    event.eventType === "StatusChange" ||
    event.eventType === "AssignmentChange" ||
    event.eventType === "PriorityChange" ||
    event.eventType === "QueueChange" ||
    event.eventType === "CategoryChange" ||
    event.eventType === "CompanyAssignment" ||
    event.eventType === "RequesterChange" ||
    event.eventType === "Created";

  const updateMutation = useMutation({
    mutationFn: () => {
      const div = document.createElement("div");
      div.innerHTML = draftHtml;
      const plainText = div.textContent ?? "";
      return ticketApi.updateEvent(ticketId, event.id, {
        bodyHtml: draftHtml,
        bodyText: plainText,
        isInternal: draftInternal,
      });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      toast.success("Event updated");
      setEditing(false);
    },
    onError: () => toast.error("Failed to update event"),
  });

  const pinMutation = useMutation({
    mutationFn: (remark: string) =>
      ticketApi.pinEvent(ticketId, event.id, remark),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      toast.success("Event pinned");
      setPinDialogOpen(false);
      setPinRemark("");
    },
    onError: () => toast.error("Failed to pin event"),
  });

  const unpinMutation = useMutation({
    mutationFn: () => ticketApi.unpinEvent(ticketId, event.id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      toast.success("Event unpinned");
    },
    onError: () => toast.error("Failed to unpin event"),
  });

  const startEdit = () => {
    setDraftHtml(event.bodyHtml ?? event.bodyText ?? "");
    setDraftInternal(event.isInternal);
    setEditorKey((k) => k + 1);
    setEditing(true);
  };

  const cancelEdit = () => {
    setDraftHtml(event.bodyHtml ?? event.bodyText ?? "");
    setDraftInternal(event.isInternal);
    setEditing(false);
  };

  return (
    <div id={`event-${event.id}`} className="relative pl-6">
      <span
        className={cn(
          "absolute -left-[9px] top-3 w-4 h-4 rounded-full border-2 border-background flex items-center justify-center",
          config.dotColor
        )}
      >
        <Icon className="w-2.5 h-2.5 text-white" />
      </span>

      {isSystemLike ? (
        <div className="flex flex-col gap-0.5 py-1">
          <div className="text-sm text-muted-foreground">
            <EventBody event={event} />
          </div>
          <span className="text-[11px] text-muted-foreground/40">
            {event.authorName && (
              <>{event.authorName} · </>
            )}
            {fmtDate(event.createdUtc)} <span className="text-muted-foreground/40">{fmtUtc(event.createdUtc)}</span>
          </span>
        </div>
      ) : (
        <div
          className={cn(
            "group glass-panel p-4",
            isPublicComment && "border-l-2 border-amber-500/50"
          )}
        >
          <div className="flex items-center justify-between gap-2 mb-2">
            <div className="flex items-center gap-2">
              <span className="text-xs font-medium text-foreground/80">
                {config.label}
              </span>
              {event.authorName && (
                <span className="text-xs text-muted-foreground/60">
                  by {event.authorName}
                </span>
              )}
              {isPublicComment && (
                <span className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-amber-500/30 bg-amber-500/10 text-amber-300">
                  Public
                </span>
              )}
              {event.editedUtc && (
                <button
                  type="button"
                  onClick={() => setRevisionOpen(true)}
                  className="text-[10px] text-muted-foreground/50 hover:text-muted-foreground transition-colors"
                >
                  (edited)
                </button>
              )}
            </div>
            <div className="flex items-center gap-1">
              <span className="text-xs text-muted-foreground shrink-0">
                {fmtDate(event.createdUtc)} <span className="text-muted-foreground/40">{fmtUtc(event.createdUtc)}</span>
              </span>
              {isPinnable && !editing && (
                <button
                  type="button"
                  onClick={() =>
                    isPinned
                      ? unpinMutation.mutate()
                      : setPinDialogOpen(true)
                  }
                  className={cn(
                    "shrink-0 p-1 rounded-md transition-all",
                    isPinned
                      ? "text-amber-400 opacity-100 hover:text-amber-300 hover:bg-amber-500/10"
                      : "text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-white/[0.06]"
                  )}
                  title={isPinned ? "Unpin" : "Pin"}
                >
                  <Pin className="h-3 w-3" />
                </button>
              )}
              {isEditable && !editing && (
                <button
                  type="button"
                  onClick={startEdit}
                  className="shrink-0 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-white/[0.06] transition-all"
                  title="Edit"
                >
                  <Pencil className="h-3 w-3" />
                </button>
              )}
            </div>
          </div>

          {editing ? (
            <div className="space-y-3">
              {/* Visibility toggle */}
              <div className="flex gap-1">
                <button
                  type="button"
                  onClick={() => setDraftInternal(true)}
                  className={cn(
                    "px-2.5 py-1 rounded-md text-xs font-medium transition-colors",
                    draftInternal
                      ? "bg-white/10 text-foreground"
                      : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]"
                  )}
                >
                  Internal
                </button>
                <button
                  type="button"
                  onClick={() => setDraftInternal(false)}
                  className={cn(
                    "px-2.5 py-1 rounded-md text-xs font-medium transition-colors",
                    !draftInternal
                      ? "bg-amber-500/15 text-amber-300 border border-amber-500/30"
                      : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]"
                  )}
                >
                  Public
                </button>
              </div>

              <RichTextEditor
                key={editorKey}
                content={draftHtml}
                onChange={setDraftHtml}
                minHeight="80px"
              />

              <div className="flex items-center justify-between">
                <button
                  type="button"
                  onClick={cancelEdit}
                  className="px-2.5 py-1 text-xs rounded-md text-muted-foreground hover:bg-white/[0.06] transition-colors"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  onClick={() => updateMutation.mutate()}
                  disabled={updateMutation.isPending}
                  className={cn(
                    "px-3 py-1.5 text-xs rounded-md font-medium transition-colors",
                    "bg-primary/20 text-primary border border-primary/30 hover:bg-primary/30",
                    updateMutation.isPending && "opacity-50 cursor-not-allowed"
                  )}
                >
                  {updateMutation.isPending ? "Saving..." : "Save"}
                </button>
              </div>
            </div>
          ) : (
            <EventBody event={event} />
          )}
        </div>
      )}

      {revisionOpen && (
        <EventRevisionDialog
          ticketId={ticketId}
          eventId={event.id}
          open={revisionOpen}
          onOpenChange={setRevisionOpen}
        />
      )}

      <Dialog open={pinDialogOpen} onOpenChange={setPinDialogOpen}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>Pin event</DialogTitle>
          </DialogHeader>
          <div className="space-y-3">
            <div>
              <label className="text-xs text-muted-foreground mb-1 block">
                Remark (optional)
              </label>
              <input
                type="text"
                value={pinRemark}
                onChange={(e) => setPinRemark(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") pinMutation.mutate(pinRemark);
                }}
                placeholder="Why is this important?"
                className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/50 outline-none focus:border-primary/50"
                autoFocus
              />
            </div>
            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setPinDialogOpen(false)}
                className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-white/[0.06] transition-colors"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => pinMutation.mutate(pinRemark)}
                disabled={pinMutation.isPending}
                className="px-3 py-1.5 text-xs rounded-md bg-primary/20 text-primary border border-primary/30 hover:bg-primary/30 font-medium transition-colors"
              >
                {pinMutation.isPending ? "Pinning..." : "Pin"}
              </button>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export { EVENT_CONFIG };

export function TicketTimeline({ ticketId, events, pinnedEventIds }: TicketTimelineProps) {
  const [preview, setPreview] = React.useState<AttachmentPreview | null>(null);
  const previewApi = React.useMemo<PreviewContextValue>(
    () => ({ open: setPreview }),
    [],
  );

  if (events.length === 0) {
    return (
      <div className="text-sm text-muted-foreground py-4">
        No events yet.
      </div>
    );
  }

  return (
    <PreviewContext.Provider value={previewApi}>
      <div className="relative border-l-2 border-white/10 space-y-4 ml-[9px]">
        {events.map((event) => (
          <TimelineEvent
            key={event.id}
            event={event}
            ticketId={ticketId}
            isPinned={pinnedEventIds.has(event.id)}
          />
        ))}
      </div>
      <AttachmentPreviewDialog
        preview={preview}
        onOpenChange={(open) => {
          if (!open) setPreview(null);
        }}
      />
    </PreviewContext.Provider>
  );
}
