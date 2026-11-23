import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { composeTemplatesApi } from "@/lib/composeTemplates-api";
import type { ComposerEditorHandle } from "./composerTypes";

/// The editor handle, its ready flag, the auto-insert guard and the token map
/// for the `::` template picker.
export function useComposerEditor({ ticketId, expanded }: { ticketId: string; expanded: boolean }) {
  // v0.0.35-F — handed by RichTextEditor.onEditorReady; used by the
  // "Import registered time" button to insert pre-rendered HTML at the
  // current selection. Local-ref instead of state because we don't need
  // to re-render when the editor mounts. v0.0.42: also used by the
  // auto-insert template flow to setContent on the empty composer.
  const editorRef = React.useRef<ComposerEditorHandle | null>(null);
  // v0.0.42 — flips to true via onEditorReady so the auto-insert effect
  // can wait for a mounted editor before calling setContent. Using state
  // (not a ref) so the effect re-runs once the editor is wired up.
  const [editorReady, setEditorReady] = React.useState(false);
  // v0.0.42 — guards against re-firing the auto-insert lookup on every
  // re-render after a successful prefill. Resets when the composer
  // collapses so the next open attempts again on an empty body.
  const autoInsertAttemptedRef = React.useRef(false);

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

  return { editorRef, editorReady, setEditorReady, autoInsertAttemptedRef, tokensQuery, composeTokens };
}
