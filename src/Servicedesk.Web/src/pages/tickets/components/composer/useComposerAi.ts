import * as React from "react";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  claudeTicketApi,
  ApiError,
  type ClaudeProposalSuccess,
  type ClaudeStatusSuccess,
  type ClaudeSummarySuccess,
  type ClaudeTicketImage,
} from "@/lib/claude-api";
import { isEmptyHtml, type ComposerEditorHandle, type TabType } from "./composerTypes";

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

/// AI actions of the composer: proposal (with screenshot picker), one-click
/// summary (with replace-confirm) and the server-posted status update.
export function useComposerAi({
  ticketId,
  bodyHtml,
  setBodyHtml,
  setTab,
  setExpanded,
  editorRef,
  updateDraft,
}: {
  ticketId: string;
  bodyHtml: string;
  setBodyHtml: React.Dispatch<React.SetStateAction<string>>;
  setTab: React.Dispatch<React.SetStateAction<TabType>>;
  setExpanded: React.Dispatch<React.SetStateAction<boolean>>;
  editorRef: React.MutableRefObject<ComposerEditorHandle | null>;
  updateDraft: (html: string, currentTab: TabType, callIsInternal?: boolean) => void;
}) {
  const queryClient = useQueryClient();
  const [aiLoading, setAiLoading] = React.useState(false);
  const [aiImages, setAiImages] = React.useState<ClaudeTicketImage[] | null>(null);
  const [aiImageDialogOpen, setAiImageDialogOpen] = React.useState(false);
  const [aiSelectedIds, setAiSelectedIds] = React.useState<string[]>([]);
  // v0.1.20 — one-click AI summary (fills the admin template). Asks before
  // replacing a composer that already has text.
  const [summaryLoading, setSummaryLoading] = React.useState(false);
  const [summaryConfirmOpen, setSummaryConfirmOpen] = React.useState(false);
  // v0.1.46 — "Status update by AI": the server posts the brief as an
  // internal note itself; the composer is never touched.
  const [statusLoading, setStatusLoading] = React.useState(false);
  const aiBusy = aiLoading || summaryLoading || statusLoading;

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

  async function runAiStatus() {
    if (aiBusy) return;
    setStatusLoading(true);
    try {
      const result = await claudeTicketApi.createTicketStatus(ticketId);

      if ("refused" in result && result.refused) {
        toast.warning(result.message);
        return;
      }

      const { costMicroEur } = result as ClaudeStatusSuccess;
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      toast.success(`AI status update posted as an internal note (€${(costMicroEur / 1_000_000).toFixed(4)})`);
    } catch (err) {
      showAiError(err, "AI status update failed");
    } finally {
      setStatusLoading(false);
    }
  }

  function handleSummary() {
    if (aiBusy) return;
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

  return {
    aiLoading,
    summaryLoading,
    statusLoading,
    aiBusy,
    aiImages,
    aiImageDialogOpen,
    setAiImageDialogOpen,
    aiSelectedIds,
    setAiSelectedIds,
    summaryConfirmOpen,
    setSummaryConfirmOpen,
    runAiProposal,
    runAiSummary,
    runAiStatus,
    handleSummary,
    handleAskAi,
  };
}
