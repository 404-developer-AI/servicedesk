import { Node, mergeAttributes } from "@tiptap/core";
import {
  NodeViewWrapper,
  ReactNodeViewRenderer,
  type NodeViewProps,
} from "@tiptap/react";
import { useMemo } from "react";

// The serialised marker attribute. The node is an atom, so getHTML() emits a
// BARE <div data-sd-signature="1"></div> at the agent's chosen spot (directly
// under their message, above the quoted history). The send path
// (OutboundMailService) swaps that marker for the authoritative cid-rendered
// signature; the rich data-URI preview lives only inside the editor.
const SIGNATURE_MARKER_ATTR = "data-sd-signature";

/// Wrap server-rendered signature preview HTML in the marker the editor parses
/// into a fixed SignatureBlock. The preview HTML (with inline data: images)
/// becomes the node's `previewHtml` attribute; it is never re-serialised.
export function signatureMarkerHtml(previewHtml: string): string {
  return `<div ${SIGNATURE_MARKER_ATTR}="1">${previewHtml}</div>`;
}

/// The bare marker the editor serialises a SignatureBlock to — what the send
/// payload actually carries. Used to build the send-body string in parallel
/// with the rich editor content.
export function signatureMarkerBare(): string {
  return `<div ${SIGNATURE_MARKER_ATTR}="1"></div>`;
}

function SignatureBlockView({ node }: NodeViewProps) {
  const previewHtml = (node.attrs.previewHtml as string) ?? "";
  // Memoise BOTH the string and the {__html} wrapper so React never reassigns
  // innerHTML on unrelated editor transactions — otherwise the inline data:
  // images would be re-decoded on every keystroke.
  const inner = useMemo(() => ({ __html: previewHtml }), [previewHtml]);

  return (
    <NodeViewWrapper
      as="div"
      className="sd-signature-block"
      contentEditable={false}
      draggable={false}
    >
      <div className="sd-signature-block__label">Signature · read-only</div>
      <div
        className="sd-signature-block__paper"
        // eslint-disable-next-line react/no-danger
        dangerouslySetInnerHTML={inner}
      />
    </NodeViewWrapper>
  );
}

/// A fixed, read-only block that shows the agent's resolved signature in the
/// compose window at the position it will occupy in the sent mail. Atom: the
/// agent can select and delete it, but never type inside it.
export const SignatureBlock = Node.create({
  name: "signatureBlock",
  group: "block",
  atom: true,
  selectable: true,
  draggable: false,

  addAttributes() {
    return {
      previewHtml: {
        default: "",
        // Capture the marker's inner HTML at parse time — the atom node has no
        // content, so ProseMirror won't descend into the signature tables.
        parseHTML: (el: HTMLElement) => el.innerHTML,
        // Never serialise the preview back out; renderHTML emits a bare marker.
        renderHTML: () => ({}),
      },
    };
  },

  parseHTML() {
    return [{ tag: `div[${SIGNATURE_MARKER_ATTR}]` }];
  },

  renderHTML() {
    return ["div", mergeAttributes({ [SIGNATURE_MARKER_ATTR]: "1" })];
  },

  addNodeView() {
    return ReactNodeViewRenderer(SignatureBlockView);
  },
});
