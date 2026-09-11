import DOMPurify from "dompurify";

/// Every user-authored HTML body the app renders (mail bodies, notes, KB
/// articles, the revision and title-review previews) passes through
/// DOMPurify, whose default attribute allow-list drops `target` — so a link
/// in a customer mail opened in the servicedesk tab itself and navigated the
/// agent away from the app. This hook re-stamps `target="_blank"` on every
/// anchor with an absolute http(s) href after sanitising, plus
/// `rel="noopener noreferrer"` so the new tab cannot reach back into the app
/// (reverse tabnabbing) and never receives the ticket URL as a referrer.
/// Relative hrefs (in-app links such as KB cross-references) keep the default
/// in-tab navigation; mailto/tel links do not need a target. Registered once
/// at startup — DOMPurify keeps attributes added in afterSanitizeAttributes,
/// so callers need no config change. The customer portal gets the same rule
/// server-side from PortalHtmlSanitizer.
export function installExternalLinkTargets(): void {
  DOMPurify.addHook("afterSanitizeAttributes", (node) => {
    if (node.nodeName !== "A") return;
    const href = node.getAttribute("href");
    if (!href || !/^https?:\/\//i.test(href)) return;
    node.setAttribute("target", "_blank");
    node.setAttribute("rel", "noopener noreferrer");
  });
}
