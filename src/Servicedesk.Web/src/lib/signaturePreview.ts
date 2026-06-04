// Client-side signature preview renderer. A faithful mirror of the server's
// SignatureRenderer (Servicedesk.Infrastructure.Signatures.SignatureRenderer)
// but in URL mode: images resolve to the asset-serve URL / photo URL instead
// of cid: parts, so the builder can show a live "what it'll look like" preview.
// The SERVER render is authoritative for what actually goes out on the wire.

import type {
  SignatureBlock,
  SignatureDesign,
  SignatureRow,
} from "@/lib/signatures-api";

const DEFAULT_FONT = "Arial, 'Helvetica Neue', Helvetica, sans-serif";
const PHOTO_VARIABLE = "agent.photo";

export interface PreviewVariables {
  fullName: string;
  firstName: string;
  lastName: string;
  jobTitle: string;
  email: string;
  phone: string;
  mobile: string;
  photoUrl: string | null;
}

/** assetUrlById maps a SignatureAsset id → its serve URL. */
export function renderSignaturePreviewHtml(
  design: SignatureDesign,
  assetUrlById: (assetId: string) => string | null,
  vars: PreviewVariables,
): string {
  const font = design.fontFamily?.trim() || DEFAULT_FONT;
  const widthStyle = design.maxWidthPx && design.maxWidthPx > 0 ? `max-width:${design.maxWidthPx}px;` : "";
  const bgStyle = design.background ? `background-color:${cssValue(design.background)};` : "";

  const rows = (design.rows ?? [])
    .map((r) => renderRow(r, assetUrlById, vars))
    .filter((h) => h.length > 0)
    .map((h) => `<tr><td style="padding:0;">${h}</td></tr>`)
    .join("");

  return (
    `<div class="sd-signature"><table role="presentation" cellpadding="0" cellspacing="0" border="0" ` +
    `style="border-collapse:collapse;${bgStyle}${widthStyle}font-family:${cssValue(font)};color:#222222;font-size:13px;line-height:1.4;">` +
    rows +
    `</table></div>`
  );
}

function renderRow(
  row: SignatureRow,
  assetUrlById: (assetId: string) => string | null,
  vars: PreviewVariables,
): string {
  const columns = row.columns ?? [];
  if (columns.length === 0) return "";

  const cells: string[] = [];
  for (const col of columns) {
    const inner = (col.blocks ?? []).map((b) => renderBlock(b, assetUrlById, vars)).join("");
    if (inner.length === 0 && columns.length === 1) continue;
    const width = col.widthPct && col.widthPct > 0 ? ` width="${col.widthPct}%"` : "";
    const valign = normalizeValign(col.valign);
    cells.push(
      `<td${width} valign="${valign}" style="padding:0;vertical-align:${valign};">${inner}</td>`,
    );
  }
  if (cells.length === 0) return "";

  return (
    `<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" ` +
    `style="border-collapse:collapse;"><tr>${cells.join("")}</tr></table>`
  );
}

function renderBlock(
  block: SignatureBlock,
  assetUrlById: (assetId: string) => string | null,
  vars: PreviewVariables,
): string {
  switch ((block.type ?? "text").toLowerCase()) {
    case "text":
      return renderText(block.html, vars, false);
    case "disclaimer":
      return renderText(block.html, vars, true);
    case "image":
      return renderImage(block, assetUrlById, vars);
    case "divider":
      return renderDivider(block);
    case "spacer":
      return renderSpacer(block);
    case "social":
      return renderSocial(block, assetUrlById);
    default:
      return "";
  }
}

function renderText(html: string | null | undefined, vars: PreviewVariables, disclaimer: boolean): string {
  if (!html || !html.trim()) return "";
  const substituted = substituteAndCollapse(html, vars);
  if (isVisuallyEmpty(substituted)) return "";
  return disclaimer
    ? `<div style="font-size:11px;line-height:1.35;color:#8a8a8a;">${substituted}</div>`
    : `<div>${substituted}</div>`;
}

function renderImage(
  block: SignatureBlock,
  assetUrlById: (assetId: string) => string | null,
  vars: PreviewVariables,
): string {
  let url: string | null = null;
  if (block.variable && block.variable.toLowerCase() === PHOTO_VARIABLE) {
    url = vars.photoUrl;
  } else if (block.assetId) {
    url = assetUrlById(block.assetId);
  }
  if (!url) return "";

  let attrs = "";
  if (block.widthPx && block.widthPx > 0) attrs += ` width="${block.widthPx}"`;
  if (block.heightPx && block.heightPx > 0) attrs += ` height="${block.heightPx}"`;
  let style = "display:block;border:0;outline:none;text-decoration:none;";
  if (block.radiusPx && block.radiusPx > 0) style += `border-radius:${block.radiusPx}px;`;
  const img = `<img src="${escapeAttr(url)}"${attrs} alt="${escapeAttr(block.alt ?? "")}" style="${style}" />`;
  if (block.href && isSafeHref(block.href)) {
    return `<a href="${escapeAttr(block.href)}" style="text-decoration:none;">${img}</a>`;
  }
  return img;
}

function renderDivider(block: SignatureBlock): string {
  const color = cssValue(block.color && block.color.trim() ? block.color : "#dddddd");
  const thickness = block.thicknessPx && block.thicknessPx > 0 ? block.thicknessPx : 1;
  const margin = block.marginPx ?? 8;
  return `<div style="border-top:${thickness}px solid ${color};font-size:0;line-height:0;margin:${margin}px 0;">&nbsp;</div>`;
}

function renderSpacer(block: SignatureBlock): string {
  const h = block.heightPx && block.heightPx > 0 ? block.heightPx : 8;
  return `<div style="height:${h}px;line-height:${h}px;font-size:0;">&nbsp;</div>`;
}

function renderSocial(block: SignatureBlock, assetUrlById: (assetId: string) => string | null): string {
  const items = block.social ?? [];
  if (items.length === 0) return "";
  let any = false;
  const cells = items
    .map((item) => {
      if (!item.url || !isSafeHref(item.url)) return "";
      let inner: string;
      const url = item.assetId ? assetUrlById(item.assetId) : null;
      if (url) {
        inner = `<img src="${escapeAttr(url)}" width="24" height="24" alt="${escapeAttr(item.network)}" style="display:block;border:0;" />`;
      } else {
        inner = escapeHtml(item.network || item.url);
      }
      any = true;
      return `<td style="padding:0 4px;"><a href="${escapeAttr(item.url)}" style="text-decoration:none;color:#222222;">${inner}</a></td>`;
    })
    .join("");
  if (!any) return "";
  return `<table role="presentation" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;"><tr>${cells}</tr></table>`;
}

const TOKENS: Array<[string, (v: PreviewVariables) => string]> = [
  ["{{agent.fullName}}", (v) => v.fullName],
  ["{{agent.firstName}}", (v) => v.firstName],
  ["{{agent.lastName}}", (v) => v.lastName],
  ["{{agent.jobTitle}}", (v) => v.jobTitle],
  ["{{agent.email}}", (v) => v.email],
  ["{{agent.phone}}", (v) => v.phone],
  ["{{agent.mobile}}", (v) => v.mobile],
];

function substituteAndCollapse(html: string, vars: PreviewVariables): string {
  const segments = html.split(/<br\s*\/?>/i);
  const kept: string[] = [];
  for (const segment of segments) {
    const present = TOKENS.filter(([t]) => segment.includes(t));
    if (present.length > 0 && present.every(([, get]) => get(vars).length === 0)) continue;
    let line = segment;
    for (const [token, get] of TOKENS) line = line.split(token).join(escapeHtml(get(vars)));
    kept.push(line);
  }
  return kept.join("<br>");
}

function isVisuallyEmpty(html: string): boolean {
  if (!html || !html.trim()) return true;
  if (/<img/i.test(html)) return false;
  const text = html.replace(/<[^>]+>/g, " ").replace(/&nbsp;/g, " ").replace(/\s+/g, " ").trim();
  return text.length === 0;
}

function normalizeValign(v: string | null | undefined): string {
  switch ((v ?? "top").toLowerCase()) {
    case "middle":
      return "middle";
    case "bottom":
      return "bottom";
    default:
      return "top";
  }
}

function isSafeHref(href: string): boolean {
  const h = href.trim().toLowerCase();
  return (
    h.startsWith("http://") ||
    h.startsWith("https://") ||
    h.startsWith("mailto:") ||
    h.startsWith("tel:")
  );
}

function cssValue(value: string): string {
  return value
    .trim()
    .replace(/["<>\\{}]/g, "");
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function escapeAttr(s: string): string {
  return escapeHtml(s);
}
