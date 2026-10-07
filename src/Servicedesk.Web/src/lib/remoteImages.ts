import DOMPurify from "dompurify";

/// v0.1.26 — external images are blocked by the CSP (`img-src 'self' data:
/// blob:`) on purpose: remote content in customer mail is a privacy leak
/// (tracking pixels) and mail clients block it too. But the browser still
/// tried to load every one of them and then POSTed a CSP report per image —
/// hundreds per hour, enough to hit the csp-report rate limit for an office
/// behind one IP, and a broken-image box that made pages jump while loading.
///
/// This hook removes the source of every image that is not same-origin
/// (absolute http(s) or protocol-relative), so nothing is requested at all.
/// The original address stays on `data-remote-src` (never fetched) and the
/// image shows its alt text with a title explaining the block; width/height
/// attributes are kept so the layout does not shift. Inline-style
/// `url(...)` backgrounds and the legacy `background` attribute pointing
/// off-site are dropped for the same reason. Registered once at startup,
/// like installExternalLinkTargets.
const REMOTE = /^(?:https?:)?\/\//i;

function isRemote(url: string | null): boolean {
  if (!url) return false;
  const trimmed = url.trim();
  if (!REMOTE.test(trimmed)) return false;
  try {
    return new URL(trimmed, window.location.href).origin !== window.location.origin;
  } catch {
    return true;
  }
}

export function stripRemoteImages(node: Element): void {
  if (node.nodeName === "IMG") {
    const src = node.getAttribute("src");
    if (isRemote(src)) {
      node.setAttribute("data-remote-src", src!.trim());
      node.removeAttribute("src");
      node.removeAttribute("srcset");
      if (!node.getAttribute("alt")) node.setAttribute("alt", "[external image]");
      node.setAttribute("title", "External image not loaded (remote content is blocked for privacy)");
      node.classList.add("sd-remote-image");
    } else if (node.getAttribute("srcset")?.split(",").some((part) => isRemote(part.trim().split(/\s+/)[0] ?? ""))) {
      node.removeAttribute("srcset");
    }
  }
  const background = node.getAttribute("background");
  if (isRemote(background)) node.removeAttribute("background");
  const style = node.getAttribute("style");
  if (style && /url\(/i.test(style)) {
    const cleaned = style.replace(/url\(\s*(['"]?)([^'")]*)\1\s*\)/gi, (match, _q, url: string) =>
      isRemote(url) ? "none" : match,
    );
    if (cleaned !== style) node.setAttribute("style", cleaned);
  }
}

export function installRemoteImageBlock(): void {
  DOMPurify.addHook("afterSanitizeAttributes", (node) => {
    if (node instanceof Element) stripRemoteImages(node);
  });
}
