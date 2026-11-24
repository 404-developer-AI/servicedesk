import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Clock, Mail, Phone, StickyNote } from "lucide-react";
import { mentionApi } from "@/lib/mentions-api";
import { agentQueueApi } from "@/lib/queue-access-api";
import { useAuth } from "@/auth/authStore";
import { RichTextEditor } from "@/components/RichTextEditor";
import { cn } from "@/lib/utils";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { SendMailForm, type MailContext } from "./SendMailForm";
import { AttachmentTray } from "./AttachmentTray";
import { useAttachmentUploads } from "../hooks/useAttachmentUploads";
import { ComposerLaunchButton } from "./composer/ComposerTabs";
import { ComposerToolbar } from "./composer/ComposerToolbar";
import { ComposerAiButtons, ComposerAiDialogs } from "./composer/ComposerAiActions";
import { fetchComposerPickerItems } from "./composer/fetchComposerPickerItems";
import { useComposerState } from "./composer/useComposerState";
import { useComposerDrafts } from "./composer/useComposerDrafts";
import { useComposerEditor } from "./composer/useComposerEditor";
import { useComposerScroll } from "./composer/useComposerScroll";
import { usePendingMailAction } from "./composer/usePendingMailAction";
import { useTemplateAutoInsert } from "./composer/useTemplateAutoInsert";
import { useComposerNavigation } from "./composer/useComposerNavigation";
import { useComposerSubmit } from "./composer/useComposerSubmit";
import { useComposerAi } from "./composer/useComposerAi";
import { useImportRegisteredTime } from "./composer/useImportRegisteredTime";

type AddNoteFormProps = {
  ticketId: string;
  /// The ticket's queue — used to scope the `::` template picker so an
  /// agent only sees templates configured for this queue (plus any
  /// unrestricted templates).
  queueId?: string | null;
  /// v0.0.42 — the ticket's current status. Used together with queueId to
  /// pick the auto-insert template for the empty internal-note composer
  /// and to filter the `::` picker by status scope.
  statusId?: string | null;
  onSubmitted: () => void;
  mailContext: MailContext;
  /// When true, the form renders for the standalone `/tickets/:id/compose`
  /// pop-out window: always expanded (no collapsed button), Cancel closes
  /// the window instead of collapsing, and the "Pop out" button is hidden
  /// (already in a popup).
  isPopup?: boolean;
  /// v0.0.105 — project tickets are internal: the "Reply" (public
  /// comment) and "Mail" options are hidden, leaving internal notes and
  /// internal call logs only, and any mail intent falls back to a note.
  /// The server refuses an outbound send regardless.
  internalOnly?: boolean;
};

export function AddNoteForm({ ticketId, queueId, statusId, onSubmitted, mailContext, isPopup = false, internalOnly = false }: AddNoteFormProps) {
  const { user } = useAuth();

  // Per-queue gate for the AI-assist button. Shares the cached accessible-queues
  // query (same key as the ticket pages). Only hide the button when we
  // positively know the queue has AI assist switched off; the endpoint enforces
  // the same rule server-side, so an undefined/loading queue still shows it.
  const { data: accessibleQueues } = useQuery({
    queryKey: ["accessible-queues"],
    queryFn: agentQueueApi.list,
    staleTime: 60_000,
  });
  const aiAssistEnabledForQueue =
    accessibleQueues?.find((q) => q.id === queueId)?.aiAssistEnabled !== false;
  const {
    expanded,
    setExpanded,
    tab,
    setTab,
    callInternal,
    setCallInternal,
    bodyHtml,
    setBodyHtml,
    initialContent,
    setInitialContent,
    editorKey,
    setEditorKey,
    mentionedUserIds,
    setMentionedUserIds,
  } = useComposerState({ ticketId, isPopup, internalOnly });
  const attachments = useAttachmentUploads(ticketId);
  const formRef = React.useRef<HTMLDivElement>(null);
  const { editorRef, editorReady, setEditorReady, autoInsertAttemptedRef, tokensQuery, composeTokens } =
    useComposerEditor({ ticketId, expanded });

  useComposerScroll(formRef, expanded, tab);
  const pendingAction = usePendingMailAction({ ticketId, internalOnly, setExpanded, setTab });
  const { updateDraft, clearDraft } = useComposerDrafts(ticketId);
  useTemplateAutoInsert({
    expanded,
    tab,
    editorReady,
    setEditorReady,
    autoInsertAttemptedRef,
    editorRef,
    queueId,
    statusId,
    bodyHtml,
    setBodyHtml,
    updateDraft,
    composeTokens,
    tokensQuery,
  });

  const isCall = tab === "call";
  const effectiveCallInternal = internalOnly || callInternal;
  const isInternal = tab === "note" || (isCall && effectiveCallInternal);

  const { switchTab, openMode } = useComposerNavigation({
    ticketId,
    internalOnly,
    tab,
    setTab,
    setExpanded,
    bodyHtml,
    setBodyHtml,
    setInitialContent,
    setMentionedUserIds,
    setEditorKey,
    setEditorReady,
    autoInsertAttemptedRef,
    updateDraft,
  });
  const { mutation, handleSubmit } = useComposerSubmit({
    ticketId,
    tab,
    isCall,
    isInternal,
    bodyHtml,
    setBodyHtml,
    mentionedUserIds,
    setMentionedUserIds,
    setEditorKey,
    setExpanded,
    attachments,
    clearDraft,
    onSubmitted,
  });
  const ai = useComposerAi({ ticketId, bodyHtml, setBodyHtml, setTab, setExpanded, editorRef, updateDraft });
  const { importing, importRegisteredTime } = useImportRegisteredTime(ticketId, editorRef);

  if (!expanded) {
    // v0.1.17 — three side-by-side entry points instead of one button.
    return (
      <div className={cn("sd-composer-launcher grid gap-2", internalOnly ? "grid-cols-2" : "grid-cols-3")}>
        <ComposerLaunchButton
          icon={StickyNote}
          label="Note"
          hint="Internal note or reply"
          accent="amber"
          onClick={() => openMode("note")}
        />
        {!internalOnly && (
          <ComposerLaunchButton
            icon={Mail}
            label="Mail"
            hint="Write a mail"
            accent="sky"
            onClick={() => openMode("mail")}
          />
        )}
        <ComposerLaunchButton
          icon={Phone}
          label="Call"
          hint="Log a phone call"
          accent="violet"
          onClick={() => openMode("call")}
        />
      </div>
    );
  }

  return (
    <div
      ref={formRef}
      className={cn(
        "glass-card p-4",
        tab === "note" && "ring-1 ring-amber-500/30",
        tab === "reply" && "ring-1 ring-emerald-500/30",
        tab === "mail" && "ring-1 ring-sky-500/30",
        tab === "call" && "ring-1 ring-violet-500/30"
      )}
    >
      <ComposerToolbar
        ticketId={ticketId}
        tab={tab}
        internalOnly={internalOnly}
        isPopup={isPopup}
        effectiveCallInternal={effectiveCallInternal}
        bodyHtml={bodyHtml}
        openMode={openMode}
        switchTab={switchTab}
        setCallInternal={setCallInternal}
        updateDraft={updateDraft}
      />

      {tab === "mail" ? (
        <SendMailForm
          ticketId={ticketId}
          queueId={queueId}
          context={mailContext}
          initialIntent={pendingAction}
          onSent={() => {
            useWorkspaceStore.getState().clearMailAction();
            // Reset the composer back to the internal-note tab after a mail
            // is sent. The component stays mounted across collapse/expand, so
            // without this the next click on the collapsed "Write an internal
            // note" button would re-open on the mail tab — risking an
            // accidental outbound mail when the agent meant to add a note.
            setTab("note");
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
          updateDraft(html, tab, effectiveCallInternal);
        }}
        placeholder={
          isCall
            ? effectiveCallInternal
              ? "Log the phone call (internal — not visible to customers). Type @@ to tag an agent, :: to insert a template or form..."
              : "Log the phone call (visible to the customer). Type @@ to tag an agent, :: to insert a template or form..."
            : isInternal
            ? "Add an internal note (not visible to customers). Type @@ to tag an agent, :: to insert a template or form..."
            : "Write a reply to the customer. Type @@ to tag an agent, :: to insert a template..."
        }
        minHeight="120px"
        onUploadFile={attachments.upload}
        onMentionQuery={(q) => mentionApi.search(q)}
        onMentionsChange={setMentionedUserIds}
        composeTokens={composeTokens}
        onIntakeQuery={(q) =>
          fetchComposerPickerItems({
            q,
            ticketId,
            queueId,
            statusId,
            tab,
            isCall,
            adsolutOrdersEnabled: user?.adsolutOrdersEnabled,
          })
        }
        onEditorReady={(editor) => {
          editorRef.current = editor as typeof editorRef.current;
          setEditorReady(true);
        }}
      />
      {tab === "reply" && (
        <div className="mt-2">
          <button
            type="button"
            disabled={importing}
            onClick={importRegisteredTime}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
              "border-violet-400/30 bg-violet-400/10 text-violet-200 hover:bg-violet-400/15",
              importing && "opacity-50 cursor-not-allowed",
            )}
            title="Insert a summary of every timesheet entry on this ticket into the reply"
          >
            <Clock className="h-3.5 w-3.5" />
            {importing ? "Loading…" : "Import registered time"}
          </button>
        </div>
      )}

      {user?.role !== "Customer" && aiAssistEnabledForQueue && !isCall && <ComposerAiButtons ai={ai} />}

      <ComposerAiDialogs ai={ai} />

      <AttachmentTray items={attachments.items} onRemove={attachments.remove} />

      <div className="mt-3 flex items-center justify-between">
        <button
          type="button"
          onClick={() => {
            clearDraft(tab);
            setBodyHtml("");
            attachments.reset();
            setEditorKey((k) => k + 1);
            if (isPopup) {
              window.close();
              return;
            }
            setExpanded(false);
          }}
          className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-glass-hover transition-colors"
        >
          Cancel
        </button>
        <button
          type="submit"
          disabled={mutation.isPending}
          className={cn(
            "px-4 py-2 rounded-md text-sm font-medium transition-colors",
            isCall
              ? "bg-violet-500/20 text-violet-300 border border-violet-500/30 hover:bg-violet-500/30"
              : !isInternal
              ? "bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 hover:bg-emerald-500/30"
              : "bg-amber-500/20 text-amber-300 border border-amber-500/30 hover:bg-amber-500/30",
            mutation.isPending && "opacity-50 cursor-not-allowed"
          )}
        >
          {mutation.isPending
            ? "Submitting..."
            : isCall
            ? "Log call"
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
