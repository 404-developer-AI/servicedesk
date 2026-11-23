import { ExternalLink, Mail, Phone, StickyNote } from "lucide-react";
import { ModeTab, SubTab } from "./ComposerTabs";
import { modeOf, type ComposerMode, type TabType } from "./composerTypes";

export function ComposerToolbar({
  ticketId,
  tab,
  internalOnly,
  isPopup,
  effectiveCallInternal,
  bodyHtml,
  openMode,
  switchTab,
  setCallInternal,
  updateDraft,
}: {
  ticketId: string;
  tab: TabType;
  internalOnly: boolean;
  isPopup: boolean;
  effectiveCallInternal: boolean;
  bodyHtml: string;
  openMode: (mode: ComposerMode) => void;
  switchTab: (next: TabType) => void;
  setCallInternal: (v: boolean) => void;
  updateDraft: (html: string, currentTab: TabType, callIsInternal?: boolean) => void;
}) {
  return (
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
  );
}
