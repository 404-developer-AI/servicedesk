import * as React from "react";
import { useQueryClient } from "@tanstack/react-query";
import { composeTemplatesApi } from "@/lib/composeTemplates-api";
import { substituteComposeTokens } from "@/lib/composeTokens";
import type { ComposerEditorHandle, TabType } from "./composerTypes";
import type { useComposerEditor } from "./useComposerEditor";

type ComposerEditor = ReturnType<typeof useComposerEditor>;

export function useTemplateAutoInsert({
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
}: {
  expanded: boolean;
  tab: TabType;
  editorReady: boolean;
  setEditorReady: React.Dispatch<React.SetStateAction<boolean>>;
  autoInsertAttemptedRef: React.MutableRefObject<boolean>;
  editorRef: React.MutableRefObject<ComposerEditorHandle | null>;
  queueId?: string | null;
  statusId?: string | null;
  bodyHtml: string;
  setBodyHtml: (html: string) => void;
  updateDraft: (html: string, currentTab: TabType, callIsInternal?: boolean) => void;
  composeTokens: ComposerEditor["composeTokens"];
  tokensQuery: ComposerEditor["tokensQuery"];
}) {
  const queryClient = useQueryClient();

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
}
