import DOMPurify from "dompurify";

// Strict allow-list for ts_headline output: Postgres only ever wraps matched
// terms in <b>...</b>. Ticket bodies that flow through normalized_text
// preserve raw < and > characters, so an attacker who plants
// "<img src=x onerror=alert(1)>" in a body would otherwise see it executed
// when the snippet is rendered via dangerouslySetInnerHTML. We strip
// everything except the highlight tag and forbid every attribute.
const SNIPPET_CONFIG = {
  ALLOWED_TAGS: ["b"],
  ALLOWED_ATTR: [],
  KEEP_CONTENT: true,
};

export function sanitizeSnippet(html: string | null | undefined): string {
  if (!html) return "";
  return DOMPurify.sanitize(html, SNIPPET_CONFIG) as unknown as string;
}

// Inbound mail HTML (e.g. Atlassian notifications) can carry a
// <base href="https://…">. DOMPurify drops the tag, but only after parsing —
// and parsing alone makes the browser evaluate it against our `base-uri
// 'self'` CSP, filing a csp_violation audit row on every open of that ticket.
// Strip it textually first so the parser never sees it. Not a security
// boundary (DOMPurify still is); purely keeps the CSP signal clean.
const BASE_TAG = /<base\b[^>]*>/gi;

export function stripBaseTags(html: string): string {
  return html.includes("<base") || html.includes("<BASE") ? html.replace(BASE_TAG, "") : html;
}

// Block-level boundaries that must not glue adjacent words together.
const BLOCK_BOUNDARY = /<(?:br|\/p|\/div|\/li|\/h[1-6]|\/tr|\/td|\/th|\/blockquote)\b[^>]*>/gi;

/// Plain-text preview of rich HTML: tags removed AND entities decoded
/// ("-&gt;" → "->"). A regex tag-strip leaves entities literal. DOMParser
/// builds an inert document, so <img> tags fire no requests and nothing runs.
export function htmlToText(html: string | null | undefined): string {
  if (!html) return "";
  const spaced = stripBaseTags(html).replace(BLOCK_BOUNDARY, "$& ");
  const text = new DOMParser().parseFromString(spaced, "text/html").body.textContent ?? "";
  return text.replace(/\s+/g, " ").trim();
}
