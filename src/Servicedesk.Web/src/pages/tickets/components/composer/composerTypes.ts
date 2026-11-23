/// v0.1.17 — the composer is split into three buttons (Note / Mail / Call).
/// `tab` keeps the sub-mode: Note covers "note" (internal) + "reply"
/// (public comment); Mail is "mail"; Call is "call" (internal or visible,
/// see `callInternal`).
export type TabType = "reply" | "note" | "mail" | "call";
export type ComposerMode = "note" | "mail" | "call";

export type ComposerEditorHandle = {
  chain: () => {
    focus: () => {
      insertContent: (html: string) => { run: () => void };
      setContent: (html: string) => { run: () => void };
    };
  };
};

export function modeOf(tab: TabType): ComposerMode {
  if (tab === "mail") return "mail";
  if (tab === "call") return "call";
  return "note";
}

export function isEmptyHtml(html: string | undefined | null) {
  return !html || !html.trim() || html === "<p></p>";
}
