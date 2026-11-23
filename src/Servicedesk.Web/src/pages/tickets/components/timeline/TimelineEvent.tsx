import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Info, ChevronDown, Pin, Pencil, ClipboardCheck, ClipboardList } from "lucide-react";
import { useServerOffsetMinutes, toServerLocal } from "@/hooks/useServerTime";
import type { NoteThreadData } from "@/lib/note-threads-api";
import { cn } from "@/lib/utils";
import { ticketApi, type TicketEvent } from "@/lib/ticket-api";
import { useAuth } from "@/auth/authStore";
import { RichTextEditor } from "@/components/RichTextEditor";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { useAttachmentUploads } from "../../hooks/useAttachmentUploads";
import { NoteThread } from "../NoteThread";
import { EventRevisionDialog } from "../EventRevisionDialog";
import { CARD_ACCENT, parseMetadata } from "../timelineMeta";
import { EVENT_CONFIG, isSystemEvent } from "./eventConfig";
import { EventBody } from "./EventBody";
import { LogFeedbackDialog } from "./LogFeedbackDialog";
import { MailHeaderPanel } from "./MailHeaderPanel";
import { parseRecipientList, type MailAddr } from "./mailAddress";

/* ─── Editable event card ─── */

const EDITABLE_TYPES = new Set(["Comment", "Note", "Mail", "Call"]);

/// v0.1.54 — replies and likes live on internal notes and internal calls
/// only (same rule as the server's NoteThreadRepository.TargetPredicate).
function isThreadable(event: TicketEvent): boolean {
  return event.eventType === "Note" || (event.eventType === "Call" && event.isInternal);
}
const PINNABLE_TYPES = new Set(["Comment", "Note", "Mail", "MailReceived", "MailSent", "Call"]);

export function TimelineEvent({
  event,
  ticketId,
  ticketNumber,
  isPinned,
  logged,
  thread,
  forceOpenReplyId,
}: {
  event: TicketEvent;
  ticketId: string;
  ticketNumber: number;
  isPinned: boolean;
  logged?: { count: number; loggers: string[] };
  /// v0.1.54 — replies + likes when this is an internal note / internal call.
  thread?: NoteThreadData;
  forceOpenReplyId?: number | null;
}) {
  const queryClient = useQueryClient();
  const { user } = useAuth();
  // Either feedback flag (full or restricted own-only) may log feedback from
  // the timeline; the backend scopes what each one can read back.
  const feedbackEnabled = (user?.feedbackEnabled || user?.feedbackOwnOnly) ?? false;
  const [logFeedbackOpen, setLogFeedbackOpen] = React.useState(false);
  const serverOffsetMinutes = useServerOffsetMinutes();
  const offset = serverOffsetMinutes ?? 0;
  const fmtDate = (iso: string) => toServerLocal(iso, offset);
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
    dotColor: "bg-glass-strong",
    label: event.eventType,
  };
  const Icon = config.icon;
  const isEditable = EDITABLE_TYPES.has(event.eventType);
  const isPinnable = PINNABLE_TYPES.has(event.eventType);
  const isPublicComment = !event.isInternal && event.eventType === "Comment";

  // v0.0.23: events that came in via a ticket merge keep their original
  // timestamps so the timeline stays chronological — but we badge them so
  // an agent can see "this came from #1234" without opening the source.
  const eventMetadata = parseMetadata(event.metadataJson);
  const mergedFromNumber = typeof eventMetadata.mergedFromTicketNumber === "number"
    ? eventMetadata.mergedFromTicketNumber
    : null;

  // v0.0.92: inbound mail flagged as auto-generated at ingest (out-of-office,
  // bounce, autoresponder). Badged so agents know no reply is expected — the
  // server also hard-suppresses trigger mail back to this sender. The signal
  // (matched header) is surfaced via the badge tooltip.
  const isAutoReply = eventMetadata.auto_reply === true;
  const autoReplySignal =
    typeof eventMetadata.auto_reply_signal === "string"
      ? eventMetadata.auto_reply_signal
      : null;
  // v0.0.102: written as one leg of a bulk action (note/comment or a
  // status/queue/priority/assignment change). Badged so an agent reading
  // the timeline knows this was "N tickets at once", not a hand-picked
  // change on this ticket alone.
  const isBulk = typeof eventMetadata.bulk_batch_id === "string";
  const isSystemLike = isSystemEvent(event);

  // Mail-header strip (From / To / Cc / Bcc), collapsed by default and toggled
  // from the event's title row. Inbound carries `from`/`fromName` as plain
  // strings from ingest; the enricher fills `to`/`cc`/`bcc` for both inbound
  // and outbound. Only render the toggle when there's something to reveal.
  const [headersOpen, setHeadersOpen] = React.useState(false);
  const isMailEvent =
    event.eventType === "MailReceived" || event.eventType === "MailSent";
  const mailFromAddr =
    typeof eventMetadata.from === "string" && eventMetadata.from.length > 0
      ? eventMetadata.from
      : null;
  const mailFromName =
    typeof eventMetadata.fromName === "string" && eventMetadata.fromName.length > 0
      ? eventMetadata.fromName
      : null;
  const mailFrom: MailAddr | null = mailFromAddr
    ? { address: mailFromAddr, name: mailFromName ?? undefined }
    : null;
  const mailTo = isMailEvent ? parseRecipientList(eventMetadata.to) : [];
  const mailCc = isMailEvent ? parseRecipientList(eventMetadata.cc) : [];
  const mailBcc = isMailEvent ? parseRecipientList(eventMetadata.bcc) : [];
  const hasMailHeaders =
    isMailEvent &&
    (!!mailFrom || mailTo.length > 0 || mailCc.length > 0 || mailBcc.length > 0);

  // Images pasted/dropped while editing upload as staged ticket attachments
  // and are linked to this event on save — same path as a new note.
  const editUploads = useAttachmentUploads(ticketId);
  const uploadEditImage = editUploads.upload;
  const uploadWhileEditing = React.useCallback(
    async (file: File) => {
      // There is no attachment tray in edit mode, so a non-image upload
      // would be invisible until save. Images only; files go via a new note.
      if (!file.type.startsWith("image/")) {
        toast.info("Only images can be added while editing. Attach files in a new note.");
        return null;
      }
      return uploadEditImage(file);
    },
    [uploadEditImage],
  );
  const uploadsPending = editUploads.items.some((it) => it.status === "pending");

  const updateMutation = useMutation({
    mutationFn: () => {
      // DOMParser: an inert document, so inline <img> tags in the draft
      // don't fire requests just to compute the plain-text twin.
      const doc = new DOMParser().parseFromString(draftHtml, "text/html");
      const plainText = doc.body.textContent ?? "";
      return ticketApi.updateEvent(ticketId, event.id, {
        bodyHtml: draftHtml,
        bodyText: plainText,
        isInternal: draftInternal,
        attachmentIds: editUploads.readyAttachmentIds,
      });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      toast.success("Event updated");
      editUploads.reset();
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
    // Uploads made during a cancelled edit stay staged (unlinked) on the
    // ticket, exactly like an abandoned new-note draft.
    editUploads.reset();
    setEditing(false);
  };

  return (
    <div id={`event-${event.id}`} className="relative pl-6">
      <span
        className={cn(
          "absolute left-[-9px] top-3 w-4 h-4 rounded-full border-2 border-background flex items-center justify-center",
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
            {(() => {
              // Trigger-fired events are written with author_user_id IS NULL
              // (system actor) and carry `triggered_by` in the metadata.
              // The SQL projection builds AuthorName via
              // `COALESCE(au.email, CONCAT_WS(' ', ac.first_name, ac.last_name))`,
              // which yields the empty string (not null) when both joins
              // miss — so a plain `?? fallback` never fires. Trim-and-test
              // first, then either use the real name or fall back to
              // "Automatic Trigger" for system-actor / triggered_by events.
              const realName = (event.authorName ?? "").trim();
              const hasTriggerSignal =
                (typeof eventMetadata.triggered_by === "string" && eventMetadata.triggered_by.length > 0)
                || event.authorUserId === null;
              const author = realName.length > 0
                ? realName
                : (hasTriggerSignal ? "Automatic Trigger" : null);
              return author ? <>{author} · </> : null;
            })()}
            {fmtDate(event.createdUtc)}
            {isBulk && (
              <span
                className="ml-2 rounded px-1.5 py-0.5 text-[10px] font-medium border border-violet-500/30 bg-violet-500/10 text-violet-300"
                title="Applied as part of a bulk action"
              >
                Bulk
              </span>
            )}
          </span>
        </div>
      ) : (
        <div
          className={cn(
            // Two visual axes on every content card:
            //  · direction → left-accent colour (+ a sky wash on inbound mail)
            //  · visibility → internal events get a warm amber ring + wash so
            //    "the customer can't see this" reads at a glance.
            "group glass-panel p-4 border-l-2 transition-colors",
            // A phone call keeps its violet accent even when internal, so
            // it never reads as a plain note; the amber wash below still
            // marks it internal.
            event.isInternal && event.eventType !== "Call"
              ? "border-l-amber-500/60"
              : CARD_ACCENT[event.eventType] ?? "border-l-glass-strong",
            // sd-internal-note is a theme hook: Steaan paints the amber-50
            // card through it, the Nebula themes leave it unstyled.
            event.isInternal
              ? "sd-internal-note ring-1 ring-amber-500/30 bg-amber-500/4"
              : event.eventType === "MailReceived" && "bg-sky-500/5"
          )}
        >
          <div className="flex items-center justify-between gap-2 mb-2">
            <div className="flex items-center gap-2">
              {hasMailHeaders ? (
                <button
                  type="button"
                  onClick={() => setHeadersOpen((o) => !o)}
                  className="flex items-center gap-1 text-xs font-medium text-foreground/80 transition-colors hover:text-foreground"
                  title={headersOpen ? "Hide mail details" : "Show mail details"}
                  aria-expanded={headersOpen}
                >
                  {config.label}
                  <ChevronDown
                    className={cn(
                      "h-3 w-3 text-muted-foreground/60 transition-transform",
                      headersOpen && "rotate-180"
                    )}
                  />
                </button>
              ) : (
                <span className="text-xs font-medium text-foreground/80">
                  {config.label}
                </span>
              )}
              {event.authorName && (
                <span className="text-xs text-muted-foreground/60">
                  by {event.authorName}
                </span>
              )}
              {isPublicComment && (
                <span className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-emerald-500/30 bg-emerald-500/10 text-emerald-300">
                  Public
                </span>
              )}
              {event.isInternal && event.eventType !== "Note" && (
                <span className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-amber-500/30 bg-amber-500/10 text-amber-300">
                  Internal
                </span>
              )}
              {isAutoReply && (
                <span
                  className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-sky-500/30 bg-sky-500/10 text-sky-300"
                  title={autoReplySignal ?? "Detected as an automatic reply"}
                >
                  Auto-reply
                </span>
              )}
              {isBulk && (
                <span
                  className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-violet-500/30 bg-violet-500/10 text-violet-300"
                  title="Posted as part of a bulk action"
                >
                  Bulk
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
              {mergedFromNumber !== null && (
                <span className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-purple-400/30 bg-purple-500/10 text-purple-200/90">
                  from #{mergedFromNumber}
                </span>
              )}
            </div>
            <div className="flex items-center gap-1">
              <span className="text-xs text-muted-foreground shrink-0">
                {fmtDate(event.createdUtc)}
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
                      : "text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-glass-hover"
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
                  className="shrink-0 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-glass-hover transition-all"
                  title="Edit"
                >
                  <Pencil className="h-3 w-3" />
                </button>
              )}
              {feedbackEnabled && !editing && (
                <button
                  type="button"
                  onClick={() => setLogFeedbackOpen(true)}
                  className={cn(
                    "shrink-0 p-1 rounded-md transition-all",
                    logged
                      ? "text-emerald-400 opacity-100 hover:text-emerald-300 hover:bg-emerald-500/10"
                      : "text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-glass-hover",
                  )}
                  title={
                    logged
                      ? `Already logged${logged.loggers.length ? ` by ${logged.loggers.join(", ")}` : ""}` +
                        (logged.count > 1 ? ` (${logged.count}×)` : "") +
                        " — click to log again"
                      : "Log employee feedback"
                  }
                >
                  {logged ? (
                    <ClipboardCheck className="h-3 w-3" />
                  ) : (
                    <ClipboardList className="h-3 w-3" />
                  )}
                </button>
              )}
            </div>
          </div>

          {feedbackEnabled && (
            <LogFeedbackDialog
              open={logFeedbackOpen}
              onOpenChange={setLogFeedbackOpen}
              ticketId={ticketId}
              ticketNumber={ticketNumber}
              eventId={event.id}
            />
          )}

          {hasMailHeaders && (
            <MailHeaderPanel
              open={headersOpen}
              from={mailFrom}
              to={mailTo}
              cc={mailCc}
              bcc={mailBcc}
            />
          )}

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
                      ? "bg-glass-strong text-foreground"
                      : "text-muted-foreground hover:text-foreground hover:bg-glass-hover"
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
                      ? "bg-emerald-500/15 text-emerald-300 border border-emerald-500/30"
                      : "text-muted-foreground hover:text-foreground hover:bg-glass-hover"
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
                preserveInlineNodes
                onUploadFile={uploadWhileEditing}
              />

              <div className="flex items-center justify-between">
                <button
                  type="button"
                  onClick={cancelEdit}
                  className="px-2.5 py-1 text-xs rounded-md text-muted-foreground hover:bg-glass-hover transition-colors"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  onClick={() => updateMutation.mutate()}
                  disabled={updateMutation.isPending || uploadsPending}
                  className={cn(
                    "px-3 py-1.5 text-xs rounded-md font-medium transition-colors",
                    "bg-primary/20 text-primary border border-primary/30 hover:bg-primary/30",
                    (updateMutation.isPending || uploadsPending) && "opacity-50 cursor-not-allowed"
                  )}
                >
                  {updateMutation.isPending ? "Saving..." : uploadsPending ? "Uploading..." : "Save"}
                </button>
              </div>
            </div>
          ) : (
            <>
              <EventBody event={event} />
              {isThreadable(event) && (
                <NoteThread ticketId={ticketId} eventId={event.id} data={thread} forceOpenReplyId={forceOpenReplyId} />
              )}
            </>
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
            <DialogDescription className="sr-only">
              Add an optional remark and pin this timeline event.
            </DialogDescription>
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
                className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/50 outline-hidden focus:border-primary/50"
                autoFocus
              />
            </div>
            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setPinDialogOpen(false)}
                className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-glass-hover transition-colors"
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
