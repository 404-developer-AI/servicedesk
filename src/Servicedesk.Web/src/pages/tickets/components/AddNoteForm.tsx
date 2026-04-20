import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { MessageCircle, ExternalLink } from "lucide-react";
import { ticketApi, userApi } from "@/lib/ticket-api";
import { preferencesApi } from "@/lib/api";
import { RichTextEditor } from "@/components/RichTextEditor";
import { cn } from "@/lib/utils";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { SendMailForm, type MailContext } from "./SendMailForm";
import { AttachmentTray } from "./AttachmentTray";
import { useAttachmentUploads } from "../hooks/useAttachmentUploads";

type AddNoteFormProps = {
  ticketId: string;
  onSubmitted: () => void;
  mailContext: MailContext;
  /// When true, the form renders for the standalone `/tickets/:id/compose`
  /// pop-out window: always expanded (no collapsed button), Cancel closes
  /// the window instead of collapsing, and the "Pop out" button is hidden
  /// (already in a popup).
  isPopup?: boolean;
};

type TabType = "reply" | "note" | "mail";

export function AddNoteForm({ ticketId, onSubmitted, mailContext, isPopup = false }: AddNoteFormProps) {
  const savedDraft = useWorkspaceStore.getState().getDraft(ticketId);
  // Popup always starts expanded — no collapsed-button state in that flow.
  const [expanded, setExpanded] = React.useState(isPopup || !!savedDraft);
  const [tab, setTab] = React.useState<TabType>(savedDraft?.tab ?? "note");
  const [bodyHtml, setBodyHtml] = React.useState(savedDraft?.bodyHtml ?? "");
  const [initialContent] = React.useState(savedDraft?.bodyHtml ?? "");
  const [editorKey, setEditorKey] = React.useState(0);
  const [mentionedUserIds, setMentionedUserIds] = React.useState<string[]>([]);
  const queryClient = useQueryClient();
  const attachments = useAttachmentUploads(ticketId);
  const formRef = React.useRef<HTMLDivElement>(null);

  // Scroll the bottom of the form flush with the viewport bottom so the
  // whole card (tabs + editor + Send/Add button) is visible after expand
  // or after switching to the taller "mail" tab. A single rAF lands
  // before Tiptap and SendMailForm finish hydrating their fields, so the
  // form grows afterwards and the Send button falls off-screen again;
  // the staggered follow-up passes catch those late reflows. "auto"
  // avoids a smooth-scroll being cancelled mid-way by the editor's
  // autofocus default-scroll.
  React.useEffect(() => {
    if (!expanded) return;
    const scrollToEnd = () => {
      formRef.current?.scrollIntoView({ behavior: "auto", block: "end" });
    };
    const raf = requestAnimationFrame(scrollToEnd);
    const timers = [80, 220].map((d) => window.setTimeout(scrollToEnd, d));
    return () => {
      cancelAnimationFrame(raf);
      timers.forEach((t) => window.clearTimeout(t));
    };
  }, [expanded, tab]);

  const pendingAction = useWorkspaceStore((s) =>
    s.pendingMailAction && s.pendingMailAction.ticketId === ticketId
      ? s.pendingMailAction
      : null,
  );

  // When the agent clicks Reply / Reply-all / Forward on a MailReceived event,
  // the event card sets pendingMailAction — we react by expanding the form and
  // switching to the mail tab. <SendMailForm> picks up the same intent via
  // props and applies it to its own state.
  React.useEffect(() => {
    if (pendingAction) {
      setExpanded(true);
      setTab("mail");
    }
  }, [pendingAction?.id, pendingAction]);

  const isInternal = tab === "note";

  // Sync draft to workspace store on editor changes. The mail tab manages its
  // own state in <SendMailForm>, so drafts only persist for note/reply.
  const updateDraft = React.useCallback(
    (html: string, currentTab: TabType) => {
      if (currentTab === "mail") return;
      const internal = currentTab === "note";
      if (html.trim() && html !== "<p></p>") {
        useWorkspaceStore
          .getState()
          .setDraft(ticketId, { bodyHtml: html, isInternal: internal, tab: currentTab });
      } else {
        useWorkspaceStore.getState().removeDraft(ticketId);
      }
    },
    [ticketId],
  );

  const clearDraft = React.useCallback(() => {
    useWorkspaceStore.getState().removeDraft(ticketId);
    preferencesApi
      .deleteWorkspaceKey(`workspace:draft:${ticketId}`)
      .catch(() => {});
  }, [ticketId]);

  const mutation = useMutation({
    mutationFn: () =>
      ticketApi.addEvent(ticketId, {
        eventType: isInternal ? "Note" : "Comment",
        bodyHtml: bodyHtml || undefined,
        isInternal,
        attachmentIds: attachments.readyAttachmentIds,
        mentionedUserIds: mentionedUserIds.length > 0 ? mentionedUserIds : undefined,
      }),
    onSuccess: () => {
      toast.success(isInternal ? "Note added" : "Reply sent");
      clearDraft();
      setBodyHtml("");
      setMentionedUserIds([]);
      attachments.reset();
      setEditorKey((k) => k + 1);
      setExpanded(false);
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      onSubmitted();
    },
    onError: () => {
      toast.error("Failed to submit — please try again");
    },
  });

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    const hasBody = bodyHtml.trim() && bodyHtml !== "<p></p>";
    const hasAttachments = attachments.readyAttachmentIds.length > 0;
    if (!hasBody && !hasAttachments) {
      toast.error("Please write something or attach a file before submitting");
      return;
    }
    if (attachments.hasPending) {
      toast.error("Wait for attachment uploads to finish");
      return;
    }
    mutation.mutate();
  }

  if (!expanded) {
    return (
      <button
        type="button"
        onClick={() => setExpanded(true)}
        className="w-full flex items-center gap-3 px-4 py-3 rounded-[var(--radius)] border border-white/10 bg-white/[0.03] text-muted-foreground/60 hover:bg-white/[0.06] hover:text-muted-foreground hover:border-white/15 transition-colors text-sm"
      >
        <MessageCircle className="h-4 w-4 shrink-0" />
        Write an internal note...
      </button>
    );
  }

  return (
    <div
      ref={formRef}
      className={cn(
        "glass-card p-4",
        tab === "reply" && "ring-1 ring-amber-500/30",
        tab === "mail" && "ring-1 ring-sky-500/30"
      )}
    >
      <div className="mb-3 flex items-center gap-1">
        <button
          type="button"
          onClick={() => {
            setTab("note");
            updateDraft(bodyHtml, "note");
          }}
          className={cn(
            "px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
            tab === "note"
              ? "bg-white/10 text-foreground"
              : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]"
          )}
        >
          Internal note
        </button>
        <button
          type="button"
          onClick={() => {
            setTab("reply");
            updateDraft(bodyHtml, "reply");
          }}
          className={cn(
            "px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
            tab === "reply"
              ? "bg-amber-500/15 text-amber-300 border border-amber-500/30"
              : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]"
          )}
        >
          Reply
        </button>
        <button
          type="button"
          onClick={() => setTab("mail")}
          className={cn(
            "px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
            tab === "mail"
              ? "bg-sky-500/15 text-sky-300 border border-sky-500/30"
              : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]"
          )}
        >
          Send mail
        </button>
        {!isPopup ? (
          <button
            type="button"
            title="Open in a separate window — lets you keep the activity feed visible while you type"
            onClick={() => {
              // Named window so a second click focuses the existing popup
              // instead of opening another one. Sized roughly like a
              // desktop mail-compose window; the user can resize further.
              const url = `/tickets/${ticketId}/compose`;
              const features = "width=900,height=820,menubar=no,toolbar=no,location=no,resizable=yes,scrollbars=yes";
              window.open(url, `sd-compose-${ticketId}`, features);
            }}
            className="ml-auto inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-white/[0.05] hover:text-foreground"
          >
            <ExternalLink className="h-3.5 w-3.5" />
            Pop out
          </button>
        ) : null}
      </div>

      {tab === "mail" ? (
        <SendMailForm
          ticketId={ticketId}
          context={mailContext}
          initialIntent={pendingAction}
          onSent={() => {
            useWorkspaceStore.getState().clearMailAction();
            setExpanded(false);
            onSubmitted();
          }}
          onCancel={() => {
            useWorkspaceStore.getState().clearMailAction();
            if (isPopup) {
              window.close();
              return;
            }
            setExpanded(false);
          }}
        />
      ) : (
        <form onSubmit={handleSubmit}>
      <RichTextEditor
        key={editorKey}
        content={initialContent || undefined}
        autoFocus
        onChange={(html) => {
          setBodyHtml(html);
          updateDraft(html, tab);
        }}
        placeholder={
          isInternal
            ? "Add an internal note (not visible to customers). Type @@ to tag an agent..."
            : "Write a reply to the customer. Type @@ to tag an agent..."
        }
        minHeight="120px"
        onUploadFile={attachments.upload}
        onMentionQuery={(q) => userApi.searchAgents(q)}
        onMentionsChange={setMentionedUserIds}
      />

      <AttachmentTray items={attachments.items} onRemove={attachments.remove} />

      <div className="mt-3 flex items-center justify-between">
        <button
          type="button"
          onClick={() => {
            clearDraft();
            setBodyHtml("");
            attachments.reset();
            setEditorKey((k) => k + 1);
            if (isPopup) {
              window.close();
              return;
            }
            setExpanded(false);
          }}
          className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-white/[0.06] transition-colors"
        >
          Cancel
        </button>
        <button
          type="submit"
          disabled={mutation.isPending}
          className={cn(
            "px-4 py-2 rounded-md text-sm font-medium transition-colors",
            !isInternal
              ? "bg-amber-500/20 text-amber-300 border border-amber-500/30 hover:bg-amber-500/30"
              : "bg-primary/20 text-primary border border-primary/30 hover:bg-primary/30",
            mutation.isPending && "opacity-50 cursor-not-allowed"
          )}
        >
          {mutation.isPending
            ? "Submitting..."
            : isInternal
            ? "Add note"
            : "Add reply"}
        </button>
      </div>
        </form>
      )}
    </div>
  );
}
