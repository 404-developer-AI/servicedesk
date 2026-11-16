import DOMPurify from "dompurify";
import { stripBaseTags } from "@/lib/sanitize";

/// Sanitising for ticket event bodies (note, call, reply, mail), shared by
/// the timeline and the tag hover preview through components/NoteBodyHtml.

/// DOMPurify config that preserves @@-mention chips. Tiptap's Mention
/// extension renders `<span data-type="mention" class="sd-mention"
/// data-id="..." data-label="...">@john</span>`. The default sanitiser
/// strips `data-*` attributes, which would flatten the chip to plain
/// text and (more importantly) destroy the agent-id needed for any
/// future hover / click-through affordance. We allow the three data
/// attrs *only* on `span` elements and do not enable HTML5-custom-data
/// on anything else (tightest scope that still lets the chip survive).
/// The "::" order pills' attrs are whitelisted explicitly too — the
/// OrderPillHost click-through and the side panel's Orders block both
/// depend on `data-order-id` surviving, and relying on DOMPurify's
/// ALLOW_DATA_ATTR default would break silently if this config is ever
/// tightened.
export const NOTE_SANITIZE_CONFIG = {
  ADD_ATTR: ["data-type", "data-id", "data-label", "data-order-id", "data-order-mention"],
};

// Lazy-load + async-decode every inline image. A content-rich imported mail
// thread can embed 150+ inline images (repeated signatures/logos across each
// quoted reply); without this the browser fires every attachment GET at once
// on open. `loading="lazy"` defers off-screen images until they scroll into
// view, `decoding="async"` keeps decode off the main thread. Set via an
// afterSanitizeAttributes hook so the attrs survive the allow-list pass
// (DOMPurify retains attributes added in this hook). Registered once at module
// load; it only ever touches <img> and mention chips, so other sanitiser
// callers are unaffected.
DOMPurify.addHook("afterSanitizeAttributes", (node) => {
  if (node.nodeName === "IMG") {
    node.setAttribute("loading", "lazy");
    node.setAttribute("decoding", "async");
  }
  // Notes saved before the editor's mention renderHTML fix carry the "@@"
  // trigger in the chip text ("@@ian"). Normalise to the label on display;
  // data-label is the source of truth (textContent assignment = no HTML).
  if (node.nodeName === "SPAN" && node.getAttribute("data-type") === "mention") {
    const label = node.getAttribute("data-label");
    if (label) node.textContent = `@${label}`;
  }
});

export function sanitizeNoteHtml(html: string): string {
  return DOMPurify.sanitize(stripBaseTags(html), NOTE_SANITIZE_CONFIG) as unknown as string;
}
