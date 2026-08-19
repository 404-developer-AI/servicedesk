import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Clock, ExternalLink, MessageCircle, Sparkles } from "lucide-react";
import { ticketApi, mentionApi } from "@/lib/ticket-api";
import { preferencesApi, agentQueueApi } from "@/lib/api";
import { useAuth } from "@/auth/authStore";
import { orderMentionItems } from "@/pages/orders/orderMention";
import { kbLinkMentionItems } from "@/pages/kb/kbLinkMention";
import { timesheetTicketApi } from "@/lib/timesheet-api";
import { composeTemplatesApi } from "@/lib/composeTemplates-api";
import { RichTextEditor, splitMentionIds } from "@/components/RichTextEditor";
import { substituteComposeTokens } from "@/lib/composeTokens";
import { cn } from "@/lib/utils";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import {
  claudeTicketApi,
  ApiError,
  type ClaudeTicketImage,
} from "@/lib/claude-api";
import { SendMailForm, type MailContext } from "./SendMailForm";
import { AttachmentTray } from "./AttachmentTray";
import { useAttachmentUploads } from "../hooks/useAttachmentUploads";

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
  /// comment) and "Send mail" tabs are hidden, leaving internal notes
  /// only, and any mail intent falls back to a note. The server refuses
  /// an outbound send regardless.
  internalOnly?: boolean;
};

type TabType = "reply" | "note" | "mail";

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
  const savedDraft = useWorkspaceStore.getState().getDraft(ticketId);
  // Mail drafts live in a separate slot (see SendMailForm) so a note and a mail
  // can both be in progress on the same ticket. Either one auto-expands the
  // composer on open; when both exist, the more recently edited picks the tab.
  const savedMailDraft = useWorkspaceStore.getState().getMailDraft(ticketId);
  // Popup always starts expanded — no collapsed-button state in that flow.
  const [expanded, setExpanded] = React.useState(
    isPopup || !!savedDraft || !!savedMailDraft,
  );
  const [tab, setTab] = React.useState<TabType>(() => {
    if (internalOnly) return "note";
    if (savedDraft && savedMailDraft) {
      return savedMailDraft.updatedUtc > savedDraft.updatedUtc
        ? "mail"
        : savedDraft.tab;
    }
    if (savedMailDraft) return "mail";
    return savedDraft?.tab ?? "note";
  });
  // Defensive: if a non-note tab was active when the ticket became
  // internal-only (e.g. it was converted to a project), fall back.
  React.useEffect(() => {
    if (internalOnly) setTab("note");
  }, [internalOnly]);
  const [bodyHtml, setBodyHtml] = React.useState(savedDraft?.bodyHtml ?? "");
  const [initialContent] = React.useState(savedDraft?.bodyHtml ?? "");
  const [editorKey, setEditorKey] = React.useState(0);
  const [mentionedUserIds, setMentionedUserIds] = React.useState<string[]>([]);
  // v0.0.35-F — handed by RichTextEditor.onEditorReady; used by the
  // "Import registered time" button to insert pre-rendered HTML at the
  // current selection. Local-ref instead of state because we don't need
  // to re-render when the editor mounts. v0.0.42: also used by the
  // auto-insert template flow to setContent on the empty composer.
  const editorRef = React.useRef<{
    chain: () => {
      focus: () => {
        insertContent: (html: string) => { run: () => void };
        setContent: (html: string) => { run: () => void };
      };
    };
  } | null>(null);
  // v0.0.42 — flips to true via onEditorReady so the auto-insert effect
  // can wait for a mounted editor before calling setContent. Using state
  // (not a ref) so the effect re-runs once the editor is wired up.
  const [editorReady, setEditorReady] = React.useState(false);
  // v0.0.42 — guards against re-firing the auto-insert lookup on every
  // re-render after a successful prefill. Resets when the composer
  // collapses so the next open attempts again on an empty body.
  const autoInsertAttemptedRef = React.useRef(false);
  const [importing, setImporting] = React.useState(false);
  const [aiLoading, setAiLoading] = React.useState(false);
  const [aiImages, setAiImages] = React.useState<ClaudeTicketImage[] | null>(null);
  const [aiImageDialogOpen, setAiImageDialogOpen] = React.useState(false);
  const [aiSelectedIds, setAiSelectedIds] = React.useState<string[]>([]);
  const queryClient = useQueryClient();
  const attachments = useAttachmentUploads(ticketId);
  const formRef = React.useRef<HTMLDivElement>(null);

  // Token map for the :: template picker — one fetch per ticket, cached
  // for the conversation. Tokens live with the ticket; mutations elsewhere
  // (contact rename, company assignment) invalidate the ticket key, which
  // is enough since the next render-cycle will refetch.
  const tokensQ = useQuery({
    queryKey: ["compose-templates", "resolve", { ticketId }],
    queryFn: () => composeTemplatesApi.resolveTokens({ ticketId }),
    staleTime: 60_000,
  });
  const composeTokens = tokensQ.data?.tokens;

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
    if (pendingAction && !internalOnly) {
      setExpanded(true);
      setTab("mail");
    }
  }, [pendingAction?.id, pendingAction, internalOnly]);

  // v0.0.42 — Auto-insert a compose template into the empty internal-note
  // composer. Fires once per expand-cycle: the moment the editor mounts on
  // the "note" tab with no existing body (and no carried-over draft), we
  // fetch the default-for-note match and drop it in via setContent. Token
  // substitution mirrors the :: picker so the prefill arrives with
  // {{contact.firstName}} already resolved — and unresolved tokens stay
  // visible so the agent can fill them in.
  React.useEffect(() => {
    if (!expanded) {
      autoInsertAttemptedRef.current = false;
      // Force the next expand to wait for a fresh onEditorReady before the
      // effect believes the editor is mounted — otherwise a stale-true
      // value from the previous expand would let the effect proceed before
      // the remounted editor's commands are wired up.
      setEditorReady(false);
      return;
    }
    if (tab !== "note") return;
    if (!editorReady) return;
    if (autoInsertAttemptedRef.current) return;
    if (!queueId || !statusId) return;
    if (bodyHtml && bodyHtml.trim() && bodyHtml !== "<p></p>") return;

    autoInsertAttemptedRef.current = true;
    let cancelled = false;

    (async () => {
      try {
        const { template } = await composeTemplatesApi.defaultForNote(
          queueId,
          statusId,
        );
        if (cancelled || !template) return;
        // Bail if the agent already started typing while the request was
        // in flight — we never overwrite content.
        if (bodyHtml && bodyHtml.trim() && bodyHtml !== "<p></p>") return;

        const html = substituteComposeTokens(template.bodyHtml, composeTokens);
        const editor = editorRef.current;
        if (!editor) return;
        editor.chain().focus().setContent(html).run();
        setBodyHtml(html);
        updateDraft(html, "note");
      } catch {
        // Silent fail — the agent can still type a note manually. A
        // missing/unreachable endpoint shouldn't block the composer.
      }
    })();

    return () => {
      cancelled = true;
    };
    // composeTokens are intentionally read via the latest closure; we don't
    // want a token-refetch to re-trigger insertion after the agent typed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [expanded, tab, editorReady, queueId, statusId]);

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
    mutationFn: () => {
      const { userIds, mailboxIds } = splitMentionIds(mentionedUserIds);
      return ticketApi.addEvent(ticketId, {
        eventType: isInternal ? "Note" : "Comment",
        bodyHtml: bodyHtml || undefined,
        isInternal,
        attachmentIds: attachments.readyAttachmentIds,
        mentionedUserIds: userIds.length > 0 ? userIds : undefined,
        mentionedMailboxIds: mailboxIds.length > 0 ? mailboxIds : undefined,
      });
    },
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

  async function runAiProposal(attachmentIds: string[]) {
    setAiLoading(true);
    try {
      const result = await claudeTicketApi.createTicketProposal(ticketId, attachmentIds);

      if ("refused" in result && result.refused) {
        toast.warning(result.message);
        return;
      }

      const { proposalHtml, costMicroEur } = result as import("@/lib/claude-api").ClaudeProposalSuccess;
      const costDisplay = `€${(costMicroEur / 1_000_000).toFixed(4)}`;

      const editor = editorRef.current;
      if (editor) {
        editor.chain().focus().setContent(proposalHtml).run();
      }
      setBodyHtml(proposalHtml);
      updateDraft(proposalHtml, "note");
      setTab("note");
      setExpanded(true);

      toast.success(`AI proposal added as a draft note (${costDisplay})`);
    } catch (err) {
      if (err instanceof ApiError) {
        const body = err.body as Record<string, unknown> | null;
        const code = body && typeof body.error === "string" ? body.error : null;
        const msg = body && typeof body.message === "string" ? body.message : null;

        if (err.status === 409) {
          const spendMicro =
            body && typeof body.monthSpendMicroEur === "number"
              ? body.monthSpendMicroEur as number
              : null;
          const budgetMicro =
            body && typeof body.monthBudgetMicroEur === "number"
              ? body.monthBudgetMicroEur as number
              : null;
          const detail =
            spendMicro !== null && budgetMicro !== null
              ? ` (spent €${(spendMicro / 1_000_000).toFixed(2)} of €${(budgetMicro / 1_000_000).toFixed(2)})`
              : "";
          toast.error((msg ?? code ?? "Budget limit reached") + detail);
        } else if (err.status === 502) {
          toast.error(`AI service error: ${msg ?? "upstream error"}`);
        } else {
          toast.error(msg ?? "AI proposal failed");
        }
      } else {
        toast.error("AI proposal failed");
      }
    } finally {
      setAiLoading(false);
    }
  }

  async function handleAskAi() {
    if (aiLoading) return;
    setAiLoading(true);
    try {
      const { items } = await claudeTicketApi.getTicketImages(ticketId);
      if (items.length === 0) {
        setAiLoading(false);
        await runAiProposal([]);
      } else {
        setAiImages(items);
        setAiSelectedIds([]);
        setAiImageDialogOpen(true);
        setAiLoading(false);
      }
    } catch {
      setAiLoading(false);
      toast.error("Failed to load ticket images");
    }
  }

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
        className="w-full flex items-center gap-3 px-4 py-3 rounded-[var(--radius)] border border-glass bg-glass text-muted-foreground/60 hover:bg-glass-hover hover:text-muted-foreground hover:border-glass-strong transition-colors text-sm"
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
        tab === "note" && "ring-1 ring-amber-500/30",
        tab === "reply" && "ring-1 ring-emerald-500/30",
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
              ? "bg-amber-500/15 text-amber-300 border border-amber-500/30"
              : "text-muted-foreground hover:text-foreground hover:bg-glass-hover"
          )}
        >
          Internal note
        </button>
        {!internalOnly && (
          <button
            type="button"
            onClick={() => {
              setTab("reply");
              updateDraft(bodyHtml, "reply");
            }}
            className={cn(
              "px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
              tab === "reply"
                ? "bg-emerald-500/15 text-emerald-300 border border-emerald-500/30"
                : "text-muted-foreground hover:text-foreground hover:bg-glass-hover"
            )}
          >
            Reply
          </button>
        )}
        {!internalOnly && (
          <button
            type="button"
            onClick={() => setTab("mail")}
            className={cn(
              "px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
              tab === "mail"
                ? "bg-sky-500/15 text-sky-300 border border-sky-500/30"
                : "text-muted-foreground hover:text-foreground hover:bg-glass-hover"
            )}
          >
            Send mail
          </button>
        )}
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
            className="ml-auto inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
          >
            <ExternalLink className="h-3.5 w-3.5" />
            Pop out
          </button>
        ) : null}
      </div>

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
          updateDraft(html, tab);
        }}
        placeholder={
          isInternal
            ? "Add an internal note (not visible to customers). Type @@ to tag an agent, :: to insert a template..."
            : "Write a reply to the customer. Type @@ to tag an agent, :: to insert a template..."
        }
        minHeight="120px"
        onUploadFile={attachments.upload}
        onMentionQuery={(q) => mentionApi.search(q)}
        onMentionsChange={setMentionedUserIds}
        composeTokens={composeTokens}
        onIntakeQuery={async (q) => {
          // Note + reply: only compose-templates surface in the picker. The
          // intake-form chip flow is mail-only (it needs a recipient + send
          // mechanism the internal-note pathway doesn't have).
          const list = await composeTemplatesApi.usable(
            queueId ?? null,
            statusId ?? null,
          );
          const needle = q.trim().toLowerCase();
          const filtered = needle
            ? list.filter(
                (t) =>
                  t.name.toLowerCase().includes(needle) ||
                  (t.description ?? "").toLowerCase().includes(needle),
              )
            : list;
          const templates = filtered.slice(0, 12).map((t) => ({
            id: t.id,
            name: t.name,
            description: t.description,
            kind: "template" as const,
            bodyHtml: t.bodyHtml,
          }));
          // v0.0.59 — also offer Adsolut orders in the `::` picker for users
          // with the Orders feature flag. Picking one inserts a clickable pill.
          // v0.0.75 — replies also offer Published KB articles as public
          // links (a reply lands in the customer's mailbox, same as mail).
          // Internal notes skip the source: a public link has no audience there.
          const [orders, kbItems] = await Promise.all([
            user?.adsolutOrdersEnabled ? orderMentionItems(q) : Promise.resolve([]),
            tab === "reply" ? kbLinkMentionItems(q) : Promise.resolve([]),
          ]);
          return [...templates, ...orders, ...kbItems];
        }}
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
            onClick={async () => {
              if (importing) return;
              setImporting(true);
              try {
                const { html } = await timesheetTicketApi.replyHtml(ticketId);
                if (!html.trim()) {
                  toast.info("No time logged on this ticket yet");
                  return;
                }
                const editor = editorRef.current;
                if (!editor) {
                  toast.error("Editor not ready, try again");
                  return;
                }
                editor.chain().focus().insertContent(html).run();
                toast.success("Time entries inserted");
              } catch {
                toast.error("Failed to load time entries");
              } finally {
                setImporting(false);
              }
            }}
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

      {user?.role !== "Customer" && aiAssistEnabledForQueue && (
        <div className="mt-2">
          <button
            type="button"
            disabled={aiLoading}
            onClick={handleAskAi}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
              "border-amber-400/30 bg-amber-400/10 text-amber-200 hover:bg-amber-400/15",
              aiLoading && "opacity-50 cursor-not-allowed",
            )}
            title="Generate a draft internal note using Claude AI based on the ticket context"
          >
            <Sparkles className="h-3.5 w-3.5" />
            {aiLoading ? "Generating…" : "Analyze & propose a solution by AI"}
          </button>
        </div>
      )}

      {aiImages !== null && (
        <AiImagePickerDialog
          open={aiImageDialogOpen}
          onOpenChange={setAiImageDialogOpen}
          images={aiImages}
          selectedIds={aiSelectedIds}
          onToggle={(id) =>
            setAiSelectedIds((prev) =>
              prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id],
            )
          }
          onGenerate={async () => {
            setAiImageDialogOpen(false);
            await runAiProposal(aiSelectedIds);
          }}
          busy={aiLoading}
        />
      )}

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
          className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-glass-hover transition-colors"
        >
          Cancel
        </button>
        <button
          type="submit"
          disabled={mutation.isPending}
          className={cn(
            "px-4 py-2 rounded-md text-sm font-medium transition-colors",
            !isInternal
              ? "bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 hover:bg-emerald-500/30"
              : "bg-amber-500/20 text-amber-300 border border-amber-500/30 hover:bg-amber-500/30",
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

function AiImagePickerDialog({
  open,
  onOpenChange,
  images,
  selectedIds,
  onToggle,
  onGenerate,
  busy,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  images: ClaudeTicketImage[];
  selectedIds: string[];
  onToggle: (id: string) => void;
  onGenerate: () => void;
  busy: boolean;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Select screenshots for AI</DialogTitle>
          <DialogDescription>
            Choose which screenshots to include with your AI proposal request.
            Uncheck any that are not relevant to reduce cost and improve focus.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3 max-h-80 overflow-y-auto">
          {images.map((img) => {
            const checked = selectedIds.includes(img.id);
            return (
              <label
                key={img.id}
                className="flex items-center gap-3 cursor-pointer rounded-lg border border-glass p-2 hover:bg-glass-hover transition-colors"
              >
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => onToggle(img.id)}
                  className="h-4 w-4 shrink-0 accent-violet-500"
                />
                <img
                  src={img.url}
                  alt={img.filename}
                  className="h-12 w-12 shrink-0 rounded object-cover border border-glass"
                />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-foreground">
                    {img.filename}
                  </p>
                  <p className="text-xs text-muted-foreground/60">
                    {img.mimeType} · {(img.sizeBytes / 1024).toFixed(0)} KB
                  </p>
                </div>
              </label>
            );
          })}
        </div>

        <DialogFooter>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onOpenChange(false)}
            disabled={busy}
          >
            Cancel
          </Button>
          <Button
            size="sm"
            disabled={busy}
            onClick={onGenerate}
            className="gap-1.5"
          >
            <Sparkles className="h-3.5 w-3.5" />
            Generate proposal
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
