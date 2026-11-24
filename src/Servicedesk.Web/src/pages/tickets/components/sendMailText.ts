import { formatDateTime } from "@/lib/dateFormat";
import { signatureMarkerHtml, signatureMarkerBare } from "@/components/SignatureBlockNode";
import { htmlToText } from "@/lib/sanitize";

/// Normalise an email for dedup + own-mailbox matching. Lowercases the
/// whole address and strips any `+suffix` on the local-part so plus-address
/// variants of a queue mailbox (e.g. `support+TCK-42@example.com`) collide
/// with the bare address (`support@example.com`). Without this the
/// "reply-all" flow would echo the plus-addressed Reply-To of the inbound
/// mail back into Cc, which is never what the agent wants.
export function normalizeAddress(addr: string): string {
  const lower = addr.trim().toLowerCase();
  const at = lower.indexOf("@");
  if (at <= 0) return lower;
  const local = lower.slice(0, at);
  const domain = lower.slice(at + 1);
  const plus = local.indexOf("+");
  const baseLocal = plus >= 0 ? local.slice(0, plus) : local;
  return `${baseLocal}@${domain}`;
}

export function ensureRePrefix(subject: string): string {
  if (!subject) return "";
  return /^re:\s*/i.test(subject) ? subject : `Re: ${subject}`;
}

export function ensureFwdPrefix(subject: string): string {
  if (!subject) return "";
  return /^(fw|fwd):\s*/i.test(subject) ? subject : `Fwd: ${subject}`;
}

export function ensureTicketTag(subject: string, ticketNumber: number): string {
  const tag = `[#${ticketNumber}]`;
  if (/\[#\d+\]/.test(subject)) return subject;
  return subject ? `${subject} ${tag}` : tag;
}

export function buildForwardQuote(source: {
  from: { address: string; name: string } | null;
  subject: string | null;
  receivedUtc: string | null;
  bodyHtml: string | null;
}): string {
  const fromLine = source.from
    ? `${source.from.name || source.from.address} &lt;${source.from.address}&gt;`
    : "(unknown sender)";
  const dateLine = source.receivedUtc
    ? formatDateTime(source.receivedUtc)
    : "";
  const subjectLine = source.subject ?? "";
  const body = source.bodyHtml ?? "";
  // No leading typing paragraphs here — assembleContent() prepends the cursor
  // line(s) (and, when enabled, the signature block) above this quote.
  return `<p>---------- Forwarded message ----------</p>
<p><strong>From:</strong> ${fromLine}<br>${dateLine ? `<strong>Date:</strong> ${dateLine}<br>` : ""}<strong>Subject:</strong> ${subjectLine}</p>
<blockquote>${body}</blockquote>`;
}

export function buildReplyQuote(source: {
  from: { address: string; name: string } | null;
  receivedUtc: string | null;
  bodyHtml: string | null;
}): string {
  const body = source.bodyHtml ?? "";
  if (!body.trim()) return "";
  const who = source.from
    ? `${source.from.name || source.from.address} &lt;${source.from.address}&gt;`
    : "(unknown sender)";
  const when = source.receivedUtc
    ? formatDateTime(source.receivedUtc)
    : "";
  const preamble = when
    ? `On ${when}, ${who} wrote:`
    : `${who} wrote:`;
  // No leading typing paragraphs here — assembleContent() prepends the cursor
  // line(s) (and, when enabled, the signature block) above this quote.
  return `<p>${preamble}</p>
<blockquote>${body}</blockquote>`;
}

/// Strip the (empty) signature marker so emptiness checks ignore a pre-loaded
/// signature — an agent who only sees their signature still hasn't written a
/// message.
export function stripSignatureMarker(html: string): string {
  return html.replace(/<div[^>]*\bdata-sd-signature\b[^>]*>\s*<\/div>/gi, "");
}

/// Re-inflate a bare signature marker — what `getHTML()` serialises, and so
/// what a saved draft carries — back into the rich preview the editor renders.
/// Display-only: the body we send keeps the bare marker for the server to swap.
/// A replacer function (not a string) is used so any `$` in the signature HTML
/// is not interpreted as a replace-pattern token. No-op without a resolved
/// signature or a marker to inflate.
export function inflateSignatureMarker(
  html: string,
  signatureHtml: string | null,
): string {
  if (!signatureHtml) return html;
  return html.replace(
    /<div[^>]*\bdata-sd-signature\b[^>]*>\s*<\/div>/i,
    () => signatureMarkerHtml(signatureHtml),
  );
}

/// Assemble the editor content from a quote (possibly empty) and the resolved
/// signature preview. Returns two strings: `rich` carries the full data-URI
/// preview inside the marker (what the editor renders) while `bare` carries the
/// bare marker (what we send + validate against — the editor serialises the
/// SignatureBlock to exactly this on send). Order is always
/// [cursor line(s)] → [signature] → [quote].
export function assembleContent(
  quote: string,
  signatureHtml: string | null,
): { rich: string; bare: string } {
  const typing = quote ? "<p></p><p></p>" : "<p></p>";
  const richMarker = signatureHtml ? signatureMarkerHtml(signatureHtml) : "";
  const bareMarker = signatureHtml ? signatureMarkerBare() : "";
  return {
    rich: typing + richMarker + quote,
    bare: typing + bareMarker + quote,
  };
}

// ---- forgotten-attachment warning (v0.0.85) ---------------------------

/// Isolate only the text the agent typed from the serialized body — never the
/// signature or the quoted/forwarded original. The composer always lays the
/// content out as [typed] → [signature marker] → [quote], so we cut at the
/// earliest of: the (bare) signature marker, the forwarded-message header, or
/// the first <blockquote>. A trailing reply preamble ("… wrote:") left just
/// before the quote is dropped too.
export function extractTypedBody(html: string): string {
  const delimiters = [
    /<div[^>]*\bdata-sd-signature\b/i,
    /----------\s*Forwarded message\s*----------/i,
    /<blockquote/i,
  ];
  let cut = html.length;
  for (const re of delimiters) {
    const i = html.search(re);
    if (i >= 0 && i < cut) cut = i;
  }
  return html.slice(0, cut).replace(/<p>[^<]*\bwrote:\s*<\/p>\s*$/i, "");
}

/// Lower-case + strip diacritics so matching is case- and accent-insensitive.
export function normalizeForMatch(s: string): string {
  return s
    .toLowerCase()
    .normalize("NFD")
    .replace(/\p{Diacritic}/gu, "");
}

export function parseKeywords(csv: string): string[] {
  return csv
    .split(",")
    .map((k) => normalizeForMatch(k.trim()))
    .filter((k) => k.length > 0);
}

/// True when the agent's own typed text contains any keyword (substring match).
export function typedTextMentionsAttachment(bodyHtml: string, keywords: string[]): boolean {
  if (keywords.length === 0) return false;
  const text = normalizeForMatch(htmlToText(extractTypedBody(bodyHtml)));
  if (!text) return false;
  return keywords.some((kw) => text.includes(kw));
}
