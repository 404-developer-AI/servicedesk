import { Mark, Node, mergeAttributes } from "@tiptap/core";
import Image from "@tiptap/extension-image";
import { TableKit } from "@tiptap/extension-table";

// Article-only Tiptap schema additions (KB editor). Everything here
// serialises to plain HTML carrying `sd-*` classes only — the server-side
// KbHtmlSanitizer keeps exactly that class namespace and strips the rest, and
// the reader styles live in index.css under `.kb-doc`. Ticket/mail composers
// never mount these, so outbound mail HTML is unchanged.

export const CALLOUT_TYPES = ["info", "tip", "warning", "important"] as const;
export type CalloutType = (typeof CALLOUT_TYPES)[number];

function calloutTypeOf(el: HTMLElement): CalloutType {
  return CALLOUT_TYPES.find((t) => el.classList.contains(`sd-callout--${t}`)) ?? "info";
}

/// Highlighted block (info / tip / warning / important) wrapping one or more
/// paragraphs or lists. Serialises to
/// `<div class="sd-callout sd-callout--warning">…</div>`; the icon and colour
/// come from CSS so the stored HTML stays language-neutral.
export const Callout = Node.create({
  name: "callout",
  group: "block",
  content: "block+",
  defining: true,

  addAttributes() {
    return {
      type: {
        default: "info" as CalloutType,
        parseHTML: (el) => calloutTypeOf(el as HTMLElement),
        // Rendered as part of the class list in renderHTML below.
        renderHTML: () => ({}),
      },
    };
  },

  parseHTML() {
    return [{ tag: "div.sd-callout" }];
  },

  renderHTML({ node, HTMLAttributes }) {
    return [
      "div",
      mergeAttributes(HTMLAttributes, {
        class: `sd-callout sd-callout--${node.attrs.type as CalloutType}`,
      }),
      0,
    ];
  },
});

/// Inline mark for names of buttons, menus and fields in step-by-step guides
/// ("click Create linked ticket") — rendered as a small key-cap so it reads
/// differently from ordinary bold emphasis.
export const UiLabel = Mark.create({
  name: "uiLabel",
  excludes: "code",

  parseHTML() {
    return [{ tag: "span.sd-ui" }];
  },

  renderHTML({ HTMLAttributes }) {
    return ["span", mergeAttributes(HTMLAttributes, { class: "sd-ui" }), 0];
  },
});

/// null = full width (the default); sm/md cap the rendered width so a tall
/// sidebar screenshot doesn't dominate the page.
export const IMAGE_SIZES = ["sm", "md"] as const;
export type ImageSize = (typeof IMAGE_SIZES)[number] | null;

function imageSizeOf(el: HTMLElement): ImageSize {
  return IMAGE_SIZES.find((s) => el.classList.contains(`sd-img--${s}`)) ?? null;
}

export const KbImage = Image.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      size: {
        default: null as ImageSize,
        parseHTML: (el) => imageSizeOf(el as HTMLElement),
        renderHTML: (attrs) => (attrs.size ? { class: `sd-img--${attrs.size}` } : {}),
      },
    };
  },
});

/// Plain tables (header row + cells). Column resizing is off: it writes
/// inline widths the sanitizer strips anyway, so the stored HTML would
/// never match what the author saw.
export const KbTable = TableKit.configure({
  table: { resizable: false },
});

export const kbArticleExtensions = [Callout, UiLabel, KbTable];
