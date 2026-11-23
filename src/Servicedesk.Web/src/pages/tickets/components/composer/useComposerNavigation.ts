import * as React from "react";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { modeOf, type ComposerMode, type TabType } from "./composerTypes";

export function useComposerNavigation({
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
}: {
  ticketId: string;
  internalOnly: boolean;
  tab: TabType;
  setTab: React.Dispatch<React.SetStateAction<TabType>>;
  setExpanded: React.Dispatch<React.SetStateAction<boolean>>;
  bodyHtml: string;
  setBodyHtml: React.Dispatch<React.SetStateAction<string>>;
  setInitialContent: React.Dispatch<React.SetStateAction<string>>;
  setMentionedUserIds: React.Dispatch<React.SetStateAction<string[]>>;
  setEditorKey: React.Dispatch<React.SetStateAction<number>>;
  setEditorReady: React.Dispatch<React.SetStateAction<boolean>>;
  autoInsertAttemptedRef: React.MutableRefObject<boolean>;
  updateDraft: (html: string, currentTab: TabType, callIsInternal?: boolean) => void;
}) {
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
    [
      internalOnly,
      tab,
      bodyHtml,
      ticketId,
      updateDraft,
      setTab,
      setBodyHtml,
      setInitialContent,
      setMentionedUserIds,
      autoInsertAttemptedRef,
      setEditorReady,
      setEditorKey,
    ],
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
    [internalOnly, switchTab, ticketId, tab, setExpanded],
  );

  return { switchTab, openMode };
}
