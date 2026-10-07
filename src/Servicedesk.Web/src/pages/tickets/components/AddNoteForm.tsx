import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Clock, ExternalLink, FileText, Mail, Phone, Sparkles, StickyNote } from "lucide-react";
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
  type ClaudeProposalSuccess,
  type ClaudeSummarySuccess,
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
  /// comment) and "Mail" options are hidden, leaving internal notes and
  /// internal call logs only, and any mail intent falls back to a note.
  /// The server refuses an outbound send regardless.
  internalOnly?: boolean;
};

/// v0.1.17 — the composer is split into three buttons (Note / Mail / Call).
/// `tab` keeps the sub-mode: Note covers "note" (internal) + "reply"
/// (public comment); Mail is "mail"; Call is "call" (internal or visible,
/// see `callInternal`).
type TabType = "reply" | "note" | "mail" | "call";
type ComposerMode = "note" | "mail" | "call";

function modeOf(tab: TabType): ComposerMode {
  if (tab === "mail") return "mail";
  if (tab === "call") return "call";
  return "note";
}

function isEmptyHtml(html: string | undefined | null) {
  return !html || !html.trim() || html === "<p></p>";
}

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
  // v0.1.17 — the Call button keeps its own draft slot too.
  const savedCallDraft = useWorkspaceStore.getState().getCallDraft(ticketId);
  // Popup always starts expanded — no collapsed-button state in that flow.
  const [expanded, setExpanded] = React.useState(
    isPopup || !!savedDraft || !!savedMailDraft || !!savedCallDraft,
  );
  const [tab, setTab] = React.useState<TabType>(() => {
    // The most recently edited draft decides where the composer reopens.
    const candidates: Array<{ tab: TabType; at: string }> = [];
    if (savedDraft) candidates.push({ tab: internalOnly ? "note" : savedDraft.tab, at: savedDraft.updatedUtc });
    if (savedMailDraft && !internalOnly) candidates.push({ tab: "mail", at: savedMailDraft.updatedUtc });
    if (savedCallDraft) candidates.push({ tab: "call", at: savedCallDraft.updatedUtc });
    if (candidates.length === 0) return "note";
    candidates.sort((a, b) => (a.at < b.at ? 1 : -1));
    return candidates[0].tab;
  });
  // v0.1.17 — Call visibility: internal by default, the agent may make the
  // call log visible to the customer (never on internal-only tickets).
  const [callInternal, setCallInternal] = React.useState(savedCallDraft?.isInternal ?? true);
  // Defensive: if a public/mail tab was active when the ticket became
  // internal-only (e.g. it was converted to a project), fall back.
  React.useEffect(() => {
    if (internalOnly) {
      setTab((t) => (t === "reply" || t === "mail" ? "note" : t));
      setCallInternal(true);
    }
  }, [internalOnly]);
  const startHtml =
    tab === "call" ? savedCallDraft?.bodyHtml ?? "" : tab === "mail" ? "" : savedDraft?.bodyHtml ?? "";
  const [bodyHtml, setBodyHtml] = React.useState(startHtml);
  const [initialContent, setInitialContent] = React.useState(startHtml);
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
  // v0.1.20 — one-click AI summary (fills the admin template). Asks before
  // replacing a composer that already has text.
  const [summaryLoading, setSummaryLoading] = React.useState(false);
  const [summaryConfirmOpen, setSummaryConfirmOpen] = React.useState(false);
  const queryClient = useQueryClient();
  const attachments = useAttachmentUploads(ticketId);
  const formRef = React.useRef<HTMLDivElement>(null);

  // Token map for the :: template picker — one fetch per ticket, cached
  // for the conversation. Tokens live with the ticket; mutations elsewhere
  // (contact rename, company assignment) invalidate the ticket key, which
  // is enough since the next render-cycle will refetch.
  // v0.1.25 — only once the composer is opened (it starts collapsed, and
  // this was one of ~26 calls on every ticket open). The auto-insert below
  // fetches the same query itself when it runs before this resolves.
  const tokensQuery = {
    queryKey: ["compose-templates", "resolve", { ticketId }],
    queryFn: () => composeTemplatesApi.resolveTokens({ ticketId }),
    staleTime: 60_000,
  };
  const tokensQ = useQuery({ ...tokensQuery, enabled: expanded });
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
    // v0.1.17 — the Call composer has its own auto-insert template.
    if (tab !== "note" && tab !== "call") return;
    if (!editorReady) return;
    if (autoInsertAttemptedRef.current) return;
    if (!queueId || !statusId) return;
    if (bodyHtml && bodyHtml.trim() && bodyHtml !== "<p></p>") return;

    autoInsertAttemptedRef.current = true;
    let cancelled = false;
    const forTab = tab;

    (async () => {
      try {
        const { template } = await (forTab === "call"
          ? composeTemplatesApi.defaultForCall(queueId, statusId)
          : composeTemplatesApi.defaultForNote(queueId, statusId));
        if (cancelled || !template) return;
        // Bail if the agent already started typing while the request was
        // in flight — we never overwrite content.
        if (bodyHtml && bodyHtml.trim() && bodyHtml !== "<p></p>") return;

        const tokens = composeTokens ?? (await queryClient.fetchQuery(tokensQuery).catch(() => null))?.tokens;
        if (cancelled) return;
        const html = substituteComposeTokens(template.bodyHtml, tokens);
        const editor = editorRef.current;
        if (!editor) return;
        editor.chain().focus().setContent(html).run();
        setBodyHtml(html);
        updateDraft(html, forTab);
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

  const isCall = tab === "call";
  const effectiveCallInternal = internalOnly || callInternal;
  const isInternal = tab === "note" || (isCall && effectiveCallInternal);

  // Sync draft to workspace store on editor changes. The mail tab manages its
  // own state in <SendMailForm>, so drafts only persist for note/reply and
  // (v0.1.17) for the call log in its own slot.
  const updateDraft = React.useCallback(
    (html: string, currentTab: TabType, callIsInternal?: boolean) => {
      if (currentTab === "mail") return;
      if (currentTab === "call") {
        const store = useWorkspaceStore.getState();
        if (!isEmptyHtml(html)) {
          store.setCallDraft(ticketId, { bodyHtml: html, isInternal: callIsInternal ?? true });
        } else {
          store.removeCallDraft(ticketId);
        }
        return;
      }
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

  const clearDraft = React.useCallback(
    (forTab: TabType) => {
      if (forTab === "call") {
        useWorkspaceStore.getState().removeCallDraft(ticketId);
        preferencesApi.deleteWorkspaceKey(`workspace:calldraft:${ticketId}`).catch(() => {});
        return;
      }
      useWorkspaceStore.getState().removeDraft(ticketId);
      preferencesApi
        .deleteWorkspaceKey(`workspace:draft:${ticketId}`)
        .catch(() => {});
    },
    [ticketId],
  );

  // v0.1.17 — move between the three composer buttons (and the Note
  // sub-tabs). Note ↔ Call swap the editor content to the other slot's
  // draft; Internal note ↔ Reply keep the text, as before.
  const switchTab = React.useCallback(
    (next: TabType) => {
      if (internalOnly && (next === "reply" || next === "mail")) next = "note";
      const fromMode = modeOf(tab);
      const toMode = modeOf(next);
      if (fromMode === toMode || toMode === "mail") {
        setTab(next);
        if (toMode === "note") updateDraft(bodyHtml, next);
        return;
      }
      const store = useWorkspaceStore.getState();
      const html =
        toMode === "call"
          ? store.getCallDraft(ticketId)?.bodyHtml ?? ""
          : store.getDraft(ticketId)?.bodyHtml ?? "";
      setTab(next);
      setBodyHtml(html);
      setInitialContent(html);
      setMentionedUserIds([]);
      autoInsertAttemptedRef.current = false;
      setEditorReady(false);
      setEditorKey((k) => k + 1);
    },
    [internalOnly, tab, bodyHtml, ticketId, updateDraft],
  );

  /// Open the composer on one of the three buttons. Note reopens the last
  /// Note sub-tab (internal note or reply).
  const openMode = React.useCallback(
    (mode: ComposerMode) => {
      const target: TabType =
        mode === "mail"
          ? "mail"
          : mode === "call"
            ? "call"
            : internalOnly
              ? "note"
              : useWorkspaceStore.getState().getDraft(ticketId)?.tab ?? (tab === "reply" ? "reply" : "note");
      switchTab(target);
      setExpanded(true);
    },
    [internalOnly, switchTab, ticketId, tab],
  );

  const mutation = useMutation({
    mutationFn: () => {
      const { userIds, mailboxIds } = splitMentionIds(mentionedUserIds);
      return ticketApi.addEvent(ticketId, {
        eventType: isCall ? "Call" : isInternal ? "Note" : "Comment",
        bodyHtml: bodyHtml || undefined,
        isInternal,
        attachmentIds: attachments.readyAttachmentIds,
        mentionedUserIds: userIds.length > 0 ? userIds : undefined,
        mentionedMailboxIds: mailboxIds.length > 0 ? mailboxIds : undefined,
      });
    },
    onSuccess: () => {
      toast.success(isCall ? "Call logged" : isInternal ? "Note added" : "Reply sent");
      clearDraft(tab);
      // A logged call can clear the ticket's Call-back flag server-side
      // (Tickets.CallbackClearOnCall) — refresh the list so the float moves.
      if (isCall) queryClient.invalidateQueries({ queryKey: ["tickets"] });
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

  /// Drops AI-generated HTML into the internal-note composer as a draft.
  function applyAiDraft(html: string) {
    const editor = editorRef.current;
    if (editor) {
      editor.chain().focus().setContent(html).run();
    }
    setBodyHtml(html);
    updateDraft(html, "note");
    setTab("note");
    setExpanded(true);
  }

  /// Shared error toast for the AI proposal + summary calls: budget/gate
  /// refusals (409, with the spent-of-budget detail), upstream errors (502)
  /// and everything else.
  function showAiError(err: unknown, fallback: string) {
    if (!(err instanceof ApiError)) {
      toast.error(fallback);
      return;
    }
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
      toast.error(msg ?? fallback);
    }
  }

  async function runAiProposal(attachmentIds: string[]) {
    setAiLoading(true);
    try {
      const result = await claudeTicketApi.createTicketProposal(ticketId, attachmentIds);

      if ("refused" in result && result.refused) {
        toast.warning(result.message);
        return;
      }

      const { proposalHtml, costMicroEur } = result as ClaudeProposalSuccess;
      applyAiDraft(proposalHtml);
      toast.success(`AI proposal added as a draft note (€${(costMicroEur / 1_000_000).toFixed(4)})`);
    } catch (err) {
      showAiError(err, "AI proposal failed");
    } finally {
      setAiLoading(false);
    }
  }

  async function runAiSummary() {
    setSummaryLoading(true);
    try {
      const result = await claudeTicketApi.createTicketSummary(ticketId);

      if ("refused" in result && result.refused) {
        toast.warning(result.message);
        return;
      }

      const { summaryHtml, costMicroEur } = result as ClaudeSummarySuccess;
      applyAiDraft(summaryHtml);
      toast.success(`AI summary added as a draft note (€${(costMicroEur / 1_000_000).toFixed(4)})`);
    } catch (err) {
      showAiError(err, "AI summary failed");
    } finally {
      setSummaryLoading(false);
    }
  }

  function handleSummary() {
    if (summaryLoading || aiLoading) return;
    if (!isEmptyHtml(bodyHtml)) {
      setSummaryConfirmOpen(true);
      return;
    }
    void runAiSummary();
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
      <div className="mb-3 flex flex-wrap items-center gap-2">
        {/* v0.1.17 — the three composer buttons, side by side. */}
        <div className="sd-composer-modes inline-flex items-center gap-0.5 rounded-lg border border-glass bg-glass p-0.5" role="tablist">
          <ModeTab icon={StickyNote} label="Note" active={modeOf(tab) === "note"} accent="amber" onClick={() => openMode("note")} />
          {!internalOnly && (
            <ModeTab icon={Mail} label="Mail" active={tab === "mail"} accent="sky" onClick={() => openMode("mail")} />
          )}
          <ModeTab icon={Phone} label="Call" active={tab === "call"} accent="violet" onClick={() => openMode("call")} />
        </div>

        {/* Sub-options of the active button. */}
        {modeOf(tab) === "note" && (
          <div className="inline-flex items-center gap-1">
            <SubTab active={tab === "note"} tone="amber" onClick={() => switchTab("note")}>
              Internal note
            </SubTab>
            {!internalOnly && (
              <SubTab active={tab === "reply"} tone="emerald" onClick={() => switchTab("reply")}>
                Reply
              </SubTab>
            )}
          </div>
        )}
        {tab === "call" && (
          <div className="inline-flex items-center gap-1">
            <SubTab
              active={effectiveCallInternal}
              tone="amber"
              onClick={() => {
                setCallInternal(true);
                updateDraft(bodyHtml, "call", true);
              }}
            >
              Internal
            </SubTab>
            {!internalOnly && (
              <SubTab
                active={!effectiveCallInternal}
                tone="emerald"
                onClick={() => {
                  setCallInternal(false);
                  updateDraft(bodyHtml, "call", false);
                }}
              >
                Visible to customer
              </SubTab>
            )}
          </div>
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
          updateDraft(html, tab, effectiveCallInternal);
        }}
        placeholder={
          isCall
            ? effectiveCallInternal
              ? "Log the phone call (internal — not visible to customers). Type @@ to tag an agent, :: to insert a template..."
              : "Log the phone call (visible to the customer). Type @@ to tag an agent, :: to insert a template..."
            : isInternal
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
          // v0.1.17 — only templates scoped for this composer button.
          const list = await composeTemplatesApi.usableCached(
            queueId ?? null,
            statusId ?? null,
            isCall ? "call" : "note",
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

      {user?.role !== "Customer" && aiAssistEnabledForQueue && !isCall && (
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <button
            type="button"
            disabled={aiLoading || summaryLoading}
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
          <button
            type="button"
            disabled={aiLoading || summaryLoading}
            onClick={handleSummary}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
              "border-amber-400/30 bg-amber-400/10 text-amber-200 hover:bg-amber-400/15",
              summaryLoading && "opacity-50 cursor-not-allowed",
            )}
            title="Generate a compact customer-facing summary of this ticket from the summary template, as a draft internal note"
          >
            <FileText className="h-3.5 w-3.5" />
            {summaryLoading ? "Summarizing…" : "Summary"}
          </button>
        </div>
      )}

      <Dialog open={summaryConfirmOpen} onOpenChange={setSummaryConfirmOpen}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>Replace the current draft?</DialogTitle>
            <DialogDescription>
              The composer already contains text. The AI summary will replace it.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button size="sm" variant="ghost" onClick={() => setSummaryConfirmOpen(false)}>
              Cancel
            </Button>
            <Button
              size="sm"
              className="gap-1.5"
              onClick={() => {
                setSummaryConfirmOpen(false);
                void runAiSummary();
              }}
            >
              <FileText className="h-3.5 w-3.5" />
              Replace with summary
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

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

// ---- v0.1.17 composer buttons ----

type Accent = "amber" | "sky" | "violet" | "emerald";

const ACCENT_ACTIVE: Record<Accent, string> = {
  amber: "bg-amber-500/15 text-amber-300 border-amber-500/30",
  sky: "bg-sky-500/15 text-sky-300 border-sky-500/30",
  violet: "bg-violet-500/15 text-violet-300 border-violet-500/30",
  emerald: "bg-emerald-500/15 text-emerald-300 border-emerald-500/30",
};

const ACCENT_ICON: Record<Accent, string> = {
  amber: "text-amber-400/80",
  sky: "text-sky-400/80",
  violet: "text-violet-400/80",
  emerald: "text-emerald-400/80",
};

/// Collapsed-state entry point: one of the three side-by-side buttons.
function ComposerLaunchButton({
  icon: Icon,
  label,
  hint,
  accent,
  onClick,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  hint: string;
  accent: Accent;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={hint}
      className="sd-composer-launch group flex min-w-0 items-center gap-2.5 rounded-(--radius) border border-glass bg-glass px-3 py-3 text-left text-sm text-muted-foreground transition-colors hover:border-glass-strong hover:bg-glass-hover hover:text-foreground"
    >
      <Icon className={cn("h-4 w-4 shrink-0 transition-colors", ACCENT_ICON[accent])} />
      <span className="min-w-0">
        <span className="block truncate font-medium text-foreground/85">{label}</span>
        <span className="block truncate text-[11px] text-muted-foreground/70">{hint}</span>
      </span>
    </button>
  );
}

/// Expanded-state switch between the three buttons.
function ModeTab({
  icon: Icon,
  label,
  active,
  accent,
  onClick,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  active: boolean;
  accent: Accent;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm font-medium transition-colors",
        active
          ? ACCENT_ACTIVE[accent]
          : "border-transparent text-muted-foreground hover:bg-glass-hover hover:text-foreground",
      )}
    >
      <Icon className="h-3.5 w-3.5" />
      {label}
    </button>
  );
}

/// Sub-option of the active button (Internal note / Reply, Internal /
/// Visible to customer).
function SubTab({
  active,
  tone,
  onClick,
  children,
}: {
  active: boolean;
  tone: Accent;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      className={cn(
        "rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
        active
          ? ACCENT_ACTIVE[tone]
          : "border-transparent text-muted-foreground hover:bg-glass-hover hover:text-foreground",
      )}
    >
      {children}
    </button>
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
