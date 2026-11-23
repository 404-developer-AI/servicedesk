import * as React from "react";
import { preferencesApi } from "@/lib/preferences-api";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { isEmptyHtml, type TabType } from "./composerTypes";

/// Draft persistence for the composer: the note/reply slot and the call slot
/// (the mail tab manages its own slot in <SendMailForm>).
export function useComposerDrafts(ticketId: string) {
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

  return { updateDraft, clearDraft };
}
