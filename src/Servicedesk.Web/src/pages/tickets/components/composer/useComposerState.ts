import * as React from "react";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import type { TabType } from "./composerTypes";

/// Expanded / tab / call-visibility / editor-content state of the composer,
/// seeded from the three draft slots (note, mail, call).
export function useComposerState({
  ticketId,
  isPopup,
  internalOnly,
}: {
  ticketId: string;
  isPopup: boolean;
  internalOnly: boolean;
}) {
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

  return {
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
  };
}
