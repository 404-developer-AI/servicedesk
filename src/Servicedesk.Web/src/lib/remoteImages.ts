import DOMPurify from "dompurify";

/// v0.1.26 — external images are blocked by the CSP (`img-src 'self' data:
/// blob:`) on purpose: remote content in customer mail is a privacy leak
/// (tracking pixels) and mail clients block it too. But the browser still
/// tried to load every one of them and then POSTed a CSP report per image —
/// hundreds per hour, enough to hit the csp-report rate limit for an office
/// behind one IP, and a broken-image box that made pages jump while loading.
///
/// These hooks take every image that is not same-origin (absolute http(s) or
/// protocol-relative) out before anything can request it (see
/// replaceRemoteImage for what replaces it). Inline-style
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

/// v0.1.33 — a blocked image is no longer an <img> without a source (the
/// browser drew a broken-image box or bare alt text): it becomes a themed
/// "External image blocked" placeholder that keeps the image's declared size,
/// and a tracking pixel (1×1, 0×0 or hidden) is dropped outright. No
/// click-to-load and no proxy — the remote address is not kept.
const PLACEHOLDER_TITLE = "External image not loaded (remote content is blocked for privacy)";
const MAX_ALT_CHARS = 80;

function dimension(node: Element, name: "width" | "height"): number | null {
  const attr = node.getAttribute(name);
  const fromStyle = new RegExp(String.raw`(?:^|;)\s*${name}\s*:\s*([\d.]+)px`, "i").exec(node.getAttribute("style") ?? "");
  const raw = attr && /^\s*[\d.]+\s*(?:px)?\s*$/i.test(attr) ? attr : fromStyle?.[1];
  if (!raw) return null;
  const value = Number.parseFloat(raw);
  return Number.isFinite(value) ? value : null;
}

function isTrackingPixel(node: Element): boolean {
  const width = dimension(node, "width");
  const height = dimension(node, "height");
  if ((width !== null && width <= 2) || (height !== null && height <= 2)) return true;
  const style = (node.getAttribute("style") ?? "").toLowerCase().replace(/\s+/g, "");
  return style.includes("display:none") || style.includes("visibility:hidden") || node.hasAttribute("hidden");
}

function placeholderFor(img: Element): HTMLElement {
  const doc = img.ownerDocument;
  const alt = (img.getAttribute("alt") ?? "").trim().slice(0, MAX_ALT_CHARS);
  const span = doc.createElement("span");
  span.className = "sd-remote-image";
  span.setAttribute("role", "img");
  span.setAttribute("title", PLACEHOLDER_TITLE);
  span.setAttribute("aria-label", alt ? `External image blocked: ${alt}` : "External image blocked");
  const width = dimension(img, "width");
  const height = dimension(img, "height");
  // Only a real image size is kept (an icon-sized one would squeeze the label).
  const sizes: string[] = [];
  if (width !== null && width >= 48) sizes.push(`width:${Math.round(width)}px`);
  if (height !== null && height >= 24) sizes.push(`height:${Math.round(height)}px`);
  if (sizes.length > 0) span.setAttribute("style", sizes.join(";"));
  span.textContent = alt || "External image blocked";
  return span;
}

/// Runs before DOMPurify looks at the element's attributes: a remote image is
/// swapped for its placeholder (or removed as a pixel) before anything can
/// fetch it. The placeholder only carries text and our own attributes.
export function replaceRemoteImage(node: Node): void {
  if (!(node instanceof Element) || node.nodeName !== "IMG") return;
  if (!isRemote(node.getAttribute("src"))) return;
  if (isTrackingPixel(node)) {
    node.remove();
    return;
  }
  node.replaceWith(placeholderFor(node));
}

export function stripRemoteImages(node: Element): void {
  if (node.nodeName === "IMG") {
    const src = node.getAttribute("src");
    if (isRemote(src)) {
      // Safety net — replaceRemoteImage normally swapped it out already.
      node.removeAttribute("src");
      node.removeAttribute("srcset");
      if (!node.getAttribute("alt")) node.setAttribute("alt", "External image blocked");
      node.setAttribute("title", PLACEHOLDER_TITLE);
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
  DOMPurify.addHook("uponSanitizeElement", (node) => replaceRemoteImage(node));
  DOMPurify.addHook("afterSanitizeAttributes", (node) => {
    if (node instanceof Element) stripRemoteImages(node);
  });
}
