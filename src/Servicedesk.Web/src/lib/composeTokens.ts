/// Replace every `{{token}}` in `html` whose resolved value in `tokens` is
/// a non-empty string. The resolved value is HTML-escaped (templates are
/// admin-authored HTML; runtime values are user-controlled strings — they
/// must not be able to inject `<script>` or break attribute quoting).
/// Tokens with an empty / missing value are left untouched so the agent
/// sees the raw `{{...}}` and notices the gap.
///
/// Regex-based so we tolerate the whitespace variants Tiptap can introduce
/// during paste (`{{ contact.firstName }}` etc.) and recognise the literal
/// placeholder regardless of how the admin typed it. The inner pattern
/// `[\w.]+` is strict on purpose — anything weirder than ASCII identifiers
/// and dots is not a placeholder we ship.
export function substituteComposeTokens(
  html: string,
  tokens: Record<string, string> | undefined,
): string {
  if (!html) return html;
  const out = html.replace(/\{\{\s*([\w.]+)\s*\}\}/g, (match, name: string) => {
    const canonical = `{{${name}}}`;
    const value = tokens?.[canonical];
    return value ? escapeHtml(value) : match;
  });

  // Diagnostic: surface unresolved placeholders during development so a
  // missing resolve endpoint / stale backend is obvious in the console
  // instead of silently shipping raw `{{...}}` into the editor.
  if (import.meta.env?.DEV) {
    const unresolved = out.match(/\{\{\s*[\w.]+\s*\}\}/g);
    if (unresolved && unresolved.length > 0) {
      console.warn(
        "[ComposeTokens] Unresolved placeholders:",
        unresolved,
        tokens
          ? `(tokens map keys: ${Object.keys(tokens).join(", ")})`
          : "(tokens map missing — is /api/compose-templates/resolve reachable? Did the backend restart after the templates feature was added?)",
      );
    }
  }

  return out;
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
