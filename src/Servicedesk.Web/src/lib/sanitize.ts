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
