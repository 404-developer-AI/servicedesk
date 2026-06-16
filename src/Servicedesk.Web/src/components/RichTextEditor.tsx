import { useEffect, useRef } from "react";
import { useEditor, EditorContent, ReactRenderer } from "@tiptap/react";
import { mergeAttributes } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";
import Link from "@tiptap/extension-link";
import Placeholder from "@tiptap/extension-placeholder";
import Image from "@tiptap/extension-image";
import Mention, { type MentionNodeAttrs } from "@tiptap/extension-mention";
import type { SuggestionOptions, SuggestionProps } from "@tiptap/suggestion";
import tippy, { type Instance as TippyInstance } from "tippy.js";
import {
  Bold,
  Italic,
  List,
  ListOrdered,
  Heading2,
  Code,
  Link as LinkIcon,
  Paperclip,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { substituteComposeTokens } from "@/lib/composeTokens";
import type { TicketAttachmentMeta, MentionPickerItem } from "@/lib/ticket-api";
import { MentionList, type MentionListHandle } from "./MentionList";
import { SignatureBlock } from "./SignatureBlockNode";
import {
  IntakeMentionList,
  type IntakeMentionListHandle,
  type IntakeMentionItem,
} from "./intake/IntakeMentionList";

type RichTextEditorProps = {
  content?: string;
  onChange?: (html: string) => void;
  placeholder?: string;
  editable?: boolean;
  minHeight?: string;
  /// Cap for the editor body when `editable` is true. Long notes / replies /
  /// mail drafts scroll inside the editor instead of pushing the submit
  /// button below the viewport. Ignored in read-only mode (ticket-body
  /// renders) where the full content should be visible inline.
  maxHeight?: string;
  className?: string;
  autoFocus?: boolean;
  /// When provided, the editor wires up paste / drag-drop / toolbar-paperclip
  /// upload paths. Image uploads are inserted inline as <img src={url}>;
  /// non-image uploads only return the meta to the caller (which renders
  /// them in an <AttachmentTray />). Returning null tells the editor the
  /// upload failed and not to insert anything.
  onUploadFile?: (file: File) => Promise<TicketAttachmentMeta | null>;
  /// When true, non-image uploads are inserted into the body as a clickable
  /// download link (instead of being dropped). Use this where the document body
  /// is the only home for attachments — e.g. KB articles, which have no separate
  /// attachment tray. Left off for tickets/mail, which render non-image uploads
  /// in their own <AttachmentTray />. Images are always inserted inline.
  linkNonImageUploads?: boolean;
  /// When provided, the editor enables the @@-mention typeahead: typing `@@`
  /// opens a popover fed by this callback (debounced to ~120ms by the editor).
  /// Selecting a row inserts a Mention node whose `id` is the agent's user-id.
  /// Called alongside onChange on every update with the current set of
  /// mentioned user-ids (deduplicated, source order).
  onMentionQuery?: (query: string) => Promise<MentionPickerItem[]>;
  onMentionsChange?: (ids: string[]) => void;
  /// v0.0.19 — parallel to onMentionQuery but for the `::` intake-form
  /// trigger. Returns the admin-managed template catalogue. Mounting the
  /// extension is opt-in so composers that don't need intake forms pay
  /// nothing for the suggestion wiring.
  onIntakeQuery?: (query: string) => Promise<IntakeMentionItem[]>;
  /// Called immediately after an agent picks a template in the `::` popover.
  /// Implementation should POST /api/tickets/{id}/intake-forms to create a
  /// Draft instance and return its id. The editor uses the returned
  /// instanceId to replace the chip's node attrs so onLinkedFormsChange
  /// can emit it on next update.
  onIntakeInsert?: (templateId: string) => Promise<string | null>;
  /// Emitted alongside onChange whenever an `::`-chip is added, replaced
  /// (after its Draft is created), or removed. Payload is the deduplicated
  /// list of instance-ids in document order.
  onLinkedFormsChange?: (instanceIds: string[]) => void;
  /// Called when the agent clicks an existing `::`-chip — the parent opens
  /// the prefill drawer for that instanceId.
  onIntakeChipClick?: (instanceId: string) => void;
  /// Resolved `{{token}}` → value map used to substitute placeholders the
  /// moment a kind="template" item is picked through the :: suggestion.
  /// Non-empty values replace their token (HTML-escaped); empty values
  /// leave the raw `{{...}}` in place so the agent sees what's missing.
  composeTokens?: Record<string, string>;
  /// v0.0.35-F — handed the live editor instance once it mounts so the
  /// parent can drive imperative commands (e.g. inserting a pre-built HTML
  /// block from a "Import registered time" button). Null is passed when
  /// the editor is being torn down on unmount.
  onEditorReady?: (editor: ReturnType<typeof useEditor> | null) => void;
  /// v0.0.61 — mount the SignatureBlock atom so a `<div data-sd-signature>`
  /// marker in the content renders as a fixed, read-only signature preview
  /// (compose window only). Off everywhere else so notes/replies never parse
  /// the marker.
  enableSignatureBlock?: boolean;
};

type ToolbarButtonProps = {
  onClick: () => void;
  active?: boolean;
  title: string;
  children: React.ReactNode;
};

function ToolbarButton({ onClick, active, title, children }: ToolbarButtonProps) {
  return (
    <button
      type="button"
      onMouseDown={(e) => {
        e.preventDefault();
        onClick();
      }}
      title={title}
      className={cn(
        "p-1.5 rounded-md transition-colors",
        active
          ? "bg-glass-strong text-foreground"
          : "text-muted-foreground hover:bg-glass-hover hover:text-foreground"
      )}
    >
      {children}
    </button>
  );
}

function ToolbarSeparator() {
  return <div className="w-px h-4 bg-glass-strong mx-1 shrink-0" />;
}

export function RichTextEditor({
  content,
  onChange,
  placeholder = "Write something…",
  editable = true,
  minHeight = "120px",
  maxHeight = "320px",
  className,
  autoFocus = false,
  onUploadFile,
  linkNonImageUploads = false,
  onMentionQuery,
  onMentionsChange,
  onIntakeQuery,
  onIntakeInsert,
  onLinkedFormsChange,
  onIntakeChipClick,
  composeTokens,
  onEditorReady,
  enableSignatureBlock = false,
}: RichTextEditorProps) {
  // Stash the latest upload callback in a ref so the editor extensions —
  // which see only the prop-snapshot at construction time — can still call
  // the freshest version after re-renders. Same trick keeps the editor from
  // being recreated when the parent's mutation state churns.
  const uploadRef = useRef<typeof onUploadFile>(onUploadFile);
  useEffect(() => {
    uploadRef.current = onUploadFile;
  }, [onUploadFile]);

  // Same ref trick for the non-image-link flag so the paste/drop handlers
  // (constructed once) read the freshest value.
  const linkNonImageRef = useRef(linkNonImageUploads);
  useEffect(() => {
    linkNonImageRef.current = linkNonImageUploads;
  }, [linkNonImageUploads]);

  const mentionQueryRef = useRef<typeof onMentionQuery>(onMentionQuery);
  useEffect(() => {
    mentionQueryRef.current = onMentionQuery;
  }, [onMentionQuery]);
  const mentionsChangeRef = useRef<typeof onMentionsChange>(onMentionsChange);
  useEffect(() => {
    mentionsChangeRef.current = onMentionsChange;
  }, [onMentionsChange]);

  const intakeQueryRef = useRef<typeof onIntakeQuery>(onIntakeQuery);
  useEffect(() => {
    intakeQueryRef.current = onIntakeQuery;
  }, [onIntakeQuery]);
  const intakeInsertRef = useRef<typeof onIntakeInsert>(onIntakeInsert);
  useEffect(() => {
    intakeInsertRef.current = onIntakeInsert;
  }, [onIntakeInsert]);
  const linkedFormsChangeRef = useRef<typeof onLinkedFormsChange>(onLinkedFormsChange);
  useEffect(() => {
    linkedFormsChangeRef.current = onLinkedFormsChange;
  }, [onLinkedFormsChange]);
  const intakeChipClickRef = useRef<typeof onIntakeChipClick>(onIntakeChipClick);
  useEffect(() => {
    intakeChipClickRef.current = onIntakeChipClick;
  }, [onIntakeChipClick]);

  // Token map is read inside the Tiptap suggestion command — that closure
  // captures props once, so we route it through a ref to stay live across
  // parent re-renders (the resolved values arrive asynchronously after
  // the editor has already mounted).
  const composeTokensRef = useRef<typeof composeTokens>(composeTokens);
  useEffect(() => {
    composeTokensRef.current = composeTokens;
  }, [composeTokens]);

  const extensions = [
    // StarterKit ships a default Link extension since Tiptap v3. We want
    // autolink on and openOnClick off, so we disable StarterKit's copy and
    // add our configured one below — otherwise Tiptap warns about the
    // duplicate `link` extension name at every editor mount.
    StarterKit.configure({ link: false }),
    Link.configure({
      autolink: true,
      openOnClick: false,
    }),
    Placeholder.configure({
      placeholder,
    }),
    // allowBase64=false is the default — we never want to embed bytes in
    // the body. Inline images always reference the upload-endpoint URL,
    // so the body stays small and the bytes live exactly once on disk.
    Image.configure({
      inline: false,
      HTMLAttributes: {
        class: "max-w-full h-auto rounded-md",
      },
    }),
  ] as Array<ReturnType<typeof StarterKit.configure> | unknown>;

  // v0.0.61 — compose-window signature block. A leaf atom that renders the
  // pre-loaded signature preview (data-URI images) and serialises to a bare
  // <div data-sd-signature> marker the send path swaps for the real signature.
  if (enableSignatureBlock) {
    extensions.push(SignatureBlock);
  }

  // Only mount the Mention extension when the caller opted into it —
  // otherwise an editor that never sees agent-mentions pays nothing for
  // this surface. The suggestion callback reads ref values so the search
  // function can update across re-renders without recreating the editor.
  if (onMentionQuery) {
    extensions.push(
      Mention.configure({
        // Mention's default renderHTML already emits
        //   <span data-type="mention" class="<our class>" data-id="..." data-label="...">@label</span>
        // via its built-in addAttributes — we just contribute the styling
        // hook and the plain-text serialisation.
        HTMLAttributes: {
          class: "sd-mention",
        },
        renderText({ node }) {
          return `@${node.attrs.label ?? node.attrs.id ?? ""}`;
        },
        suggestion: buildMentionSuggestion(mentionQueryRef),
      }),
    );
  }

  // v0.0.19 — second Mention extension keyed to `::` for intake-form
  // templates. A distinct `name` is required because Tiptap registers
  // extensions by name; sharing "mention" with the agent trigger would
  // collide. We emit `<span data-intake-form="{instanceId}">{templateName}</span>`
  // at render time — OutboundMailService recognises that marker and swaps
  // it for the real anchor when the mail goes out.
  if (onIntakeQuery) {
    // Extend Mention first so we can add our own attrs + HTML
    // serialisation (which `.configure()` does not expose), then chain
    // `.configure()` purely for the option-shape fields (HTMLAttributes
    // + suggestion wiring). The extended extension emits
    // `<span data-intake-form="{instanceId}">{templateName}</span>` at
    // render time — OutboundMailService recognises that marker and swaps
    // it for the real anchor when the mail leaves the mailbox.
    const IntakeMention = Mention.extend({
      name: "intakeMention",
      addAttributes() {
        return {
          id: {
            default: null,
            parseHTML: (el: HTMLElement) =>
              el.getAttribute("data-template-id"),
            renderHTML: (attrs: Record<string, unknown>) => ({
              "data-template-id": (attrs.id as string) ?? "",
            }),
          },
          instanceId: {
            default: null,
            parseHTML: (el: HTMLElement) =>
              el.getAttribute("data-intake-form"),
            renderHTML: (attrs: Record<string, unknown>) => {
              const iid = attrs.instanceId as string | null;
              return iid ? { "data-intake-form": iid } : {};
            },
          },
          label: {
            default: null,
            parseHTML: (el: HTMLElement) => el.getAttribute("data-label"),
            renderHTML: (attrs: Record<string, unknown>) => ({
              "data-label": (attrs.label as string) ?? "",
            }),
          },
        };
      },
      renderHTML({ node, HTMLAttributes }) {
        const label = (node.attrs.label as string) ?? "";
        // IMPORTANT: merge options.HTMLAttributes (class + data-intake-mention)
        // with the per-attribute HTMLAttributes (data-template-id / data-intake-form
        // / data-label). Tiptap does this in its default Mention.renderHTML; by
        // overriding we lose the class unless we re-merge here.
        return [
          "span",
          mergeAttributes(
            { "data-type": "intakeMention" },
            this.options.HTMLAttributes,
            HTMLAttributes,
          ),
          label,
        ];
      },
      renderText({ node }) {
        return `[${node.attrs.label ?? "Intake form"}]`;
      },
    }).configure({
      HTMLAttributes: {
        class: "sd-intake-mention",
        "data-intake-mention": "true",
      },
      suggestion: buildIntakeSuggestion(intakeQueryRef, intakeInsertRef, composeTokensRef),
    });

    extensions.push(IntakeMention);

    // v0.0.59 — order pills. A second Mention-derived inline atom keyed off
    // `data-order-id`. It has NO own suggestion — orders surface through the
    // same `::` picker (their items carry kind: "order") and the shared
    // command inserts this node. Registered so saved pills re-parse when a
    // note is re-opened for editing, and so renderHTML emits the clickable
    // `<span data-order-id="…">` that OrderPillHost listens for.
    const OrderMention = Mention.extend({
      name: "orderMention",
      addAttributes() {
        return {
          id: {
            default: null,
            parseHTML: (el: HTMLElement) => el.getAttribute("data-order-id"),
            renderHTML: (attrs: Record<string, unknown>) => ({
              "data-order-id": (attrs.id as string) ?? "",
            }),
          },
          label: {
            default: null,
            parseHTML: (el: HTMLElement) => el.getAttribute("data-label"),
            renderHTML: (attrs: Record<string, unknown>) => ({
              "data-label": (attrs.label as string) ?? "",
            }),
          },
        };
      },
      renderHTML({ node, HTMLAttributes }) {
        const label = (node.attrs.label as string) ?? "";
        return [
          "span",
          mergeAttributes(
            { "data-type": "orderMention" },
            this.options.HTMLAttributes,
            HTMLAttributes,
          ),
          label,
        ];
      },
      renderText({ node }) {
        return `[${node.attrs.label ?? "Order"}]`;
      },
      // Insert-only node — orders surface through the existing `::` picker, so
      // OrderMention must NOT register its own suggestion plugin (the inherited
      // Mention default keys off `@` and would collide with the @@-mention).
      addProseMirrorPlugins() {
        return [];
      },
    }).configure({
      HTMLAttributes: {
        class: "sd-order-mention",
        "data-order-mention": "true",
      },
    });

    extensions.push(OrderMention);
  }

  const editor = useEditor({
    autofocus: autoFocus ? "end" : false,
    extensions: extensions as never,
    content: content ?? "",
    editable,
    onUpdate({ editor: e }) {
      onChange?.(e.getHTML());
      const cb = mentionsChangeRef.current;
      if (cb) cb(extractMentionIds(e.getJSON()));
      const fb = linkedFormsChangeRef.current;
      if (fb) fb(extractIntakeInstanceIds(e.getJSON()));
    },
    editorProps: {
      handleClick(_view, _pos, event) {
        // Intake-form chips are passive DOM — Tiptap lets events bubble.
        // We look up the closest element with data-intake-form and fire
        // onIntakeChipClick so the parent opens the prefill drawer.
        const target = event.target as HTMLElement | null;
        if (!target) return false;
        // v0.0.59 — order pill: ProseMirror swallows the native click, so we
        // re-dispatch a window event that OrderPillHost listens for and opens
        // the order-detail dialog.
        const orderPill = target.closest("[data-order-id]") as HTMLElement | null;
        if (orderPill) {
          const orderId = orderPill.getAttribute("data-order-id");
          if (orderId) {
            window.dispatchEvent(new CustomEvent("sd:open-order", { detail: orderId }));
            return true;
          }
        }
        const chip = target.closest(
          "[data-intake-form]",
        ) as HTMLElement | null;
        if (!chip) return false;
        const instanceId = chip.getAttribute("data-intake-form");
        if (!instanceId) return false;
        const cb = intakeChipClickRef.current;
        if (cb) {
          cb(instanceId);
          return true;
        }
        return false;
      },
      handlePaste(_view, event) {
        if (!uploadRef.current) return false;
        const files = collectFilesFromClipboard(event.clipboardData);
        if (files.length === 0) return false;
        event.preventDefault();
        void handleFiles(files, uploadRef.current!, (meta) =>
          insertUpload(editorInstance(), meta, linkNonImageRef.current),
        );
        return true;
      },
      handleDrop(_view, event, _slice, moved) {
        // moved=true means tiptap is repositioning an existing node — let it.
        if (moved || !uploadRef.current) return false;
        const dt = (event as DragEvent).dataTransfer;
        const files = collectFilesFromDataTransfer(dt);
        if (files.length === 0) return false;
        event.preventDefault();
        void handleFiles(files, uploadRef.current!, (meta) =>
          insertUpload(editorInstance(), meta, linkNonImageRef.current),
        );
        return true;
      },
    },
  });

  // Tiptap's editor instance is captured outside the closures above via this
  // helper so handlers always reference the live editor (not a stale snapshot
  // from initial render). It's a typed read of the same `editor` we return.
  function editorInstance() {
    return editor;
  }

  // Sync content prop into the editor when it changes externally
  // (e.g. after a save updates the query cache, or when an async load
  // hydrates an edit-page that mounted before the query resolved).
  //
  // Read-only editors always sync — they only ever render upstream
  // content, never user input.
  //
  // Editable editors sync only while the editor is empty. The first
  // non-empty content arrival hydrates the composer; subsequent prop
  // changes are ignored so the user's cursor isn't clobbered mid-typing.
  // Previously editable mode skipped sync entirely, which made
  // edit-pages stay empty when the article body arrived after mount
  // (the case for imported articles whose `?include=body` query
  // resolves on the next tick).
  useEffect(() => {
    if (!editor) return;
    const current = editor.getHTML();
    const incoming = content ?? "";
    if (current === incoming) return;
    if (editable && !editor.isEmpty) return;
    editor.commands.setContent(incoming);
  }, [editor, content, editable]);

  // v0.0.35-F — hand the live editor instance to the parent once it
  // exists so it can drive imperative commands (e.g. insertContent from a
  // "Import registered time" button). Notify with null on teardown to
  // avoid stale references after the editor is destroyed.
  useEffect(() => {
    if (!onEditorReady) return;
    onEditorReady(editor);
    return () => onEditorReady(null);
  }, [editor, onEditorReady]);

  const setLink = () => {
    if (!editor) return;
    const prev = editor.getAttributes("link").href as string | undefined;
    const url = window.prompt("URL", prev ?? "");
    if (url === null) return;
    if (url === "") {
      editor.chain().focus().extendMarkRange("link").unsetLink().run();
      return;
    }
    editor.chain().focus().extendMarkRange("link").setLink({ href: url }).run();
  };

  const triggerFilePicker = () => {
    const upload = uploadRef.current;
    if (!upload) return;
    const input = document.createElement("input");
    input.type = "file";
    input.multiple = true;
    input.onchange = () => {
      const files = input.files ? Array.from(input.files) : [];
      if (files.length === 0) return;
      void handleFiles(files, upload, (meta) =>
        insertUpload(editor, meta, linkNonImageRef.current),
      );
    };
    input.click();
  };

  return (
    <>
      <style>{`
        .rte-content .ProseMirror {
          outline: none;
        }
        .rte-content .ProseMirror p {
          margin: 0.5em 0;
        }
        .rte-content .ProseMirror p:first-child {
          margin-top: 0;
        }
        .rte-content .ProseMirror p:last-child {
          margin-bottom: 0;
        }
        .rte-content .ProseMirror ul {
          padding-left: 1.5em;
          margin-left: 1em;
          list-style: disc outside;
        }
        .rte-content .ProseMirror ol {
          padding-left: 1.5em;
          margin-left: 1em;
          list-style: decimal outside;
        }
        .rte-content .ProseMirror li > p {
          margin: 0;
        }
        .rte-content .ProseMirror a {
          color: hsl(265 89% 70%);
          text-decoration: underline;
        }
        .rte-content .ProseMirror code {
          background: rgba(255,255,255,0.06);
          padding: 0.2em 0.4em;
          border-radius: 4px;
          font-size: 0.85em;
        }
        .rte-content .ProseMirror pre {
          background: rgba(255,255,255,0.06);
          padding: 0.75em 1em;
          border-radius: 6px;
          overflow-x: auto;
        }
        .rte-content .ProseMirror pre code {
          background: none;
          padding: 0;
          font-size: inherit;
        }
        .rte-content .ProseMirror blockquote {
          border-left: 3px solid rgba(255,255,255,0.15);
          padding-left: 1em;
          color: hsl(240 5% 65%);
          margin: 0.5em 0;
        }
        .rte-content .ProseMirror h2 {
          font-size: 1.25em;
          font-weight: 600;
          margin: 0.75em 0 0.25em;
        }
        /* v0.0.61 — fixed signature preview block. The signature HTML is built
           for light mail clients (dark #222 text), so it sits on a white
           "paper" surface inside a subtle glass frame, with a small caption. */
        .rte-content .ProseMirror .sd-signature-block {
          margin: 0.75em 0;
          border: 1px solid rgba(255,255,255,0.12);
          border-radius: 10px;
          padding: 0.5em;
          background: rgba(255,255,255,0.03);
          user-select: none;
        }
        .rte-content .ProseMirror .sd-signature-block.ProseMirror-selectednode {
          border-color: hsl(265 89% 70% / 0.5);
          box-shadow: 0 0 0 1px hsl(265 89% 70% / 0.3);
        }
        .rte-content .ProseMirror .sd-signature-block__label {
          font-size: 10px;
          text-transform: uppercase;
          letter-spacing: 0.08em;
          color: hsl(240 5% 55%);
          margin: 0 0 0.4em 0.15em;
        }
        .rte-content .ProseMirror .sd-signature-block__paper {
          background: #ffffff;
          border-radius: 6px;
          padding: 12px 14px;
          overflow-x: auto;
        }
        .rte-content .ProseMirror p.is-editor-empty:first-child::before {
          content: attr(data-placeholder);
          color: hsl(240 5% 45%);
          pointer-events: none;
          float: left;
          height: 0;
        }
        .rte-content .ProseMirror img {
          max-width: 100%;
          height: auto;
          margin: 0.5em 0;
          border-radius: 6px;
        }
        .rte-content .ProseMirror .sd-mention {
          display: inline-flex;
          align-items: center;
          padding: 0 0.4em;
          border-radius: 9999px;
          background: hsl(265 89% 70% / 0.15);
          color: hsl(265 89% 85%);
          border: 1px solid hsl(265 89% 70% / 0.3);
          font-weight: 500;
          font-size: 0.92em;
          white-space: nowrap;
          line-height: 1.4;
          vertical-align: baseline;
        }
        .rte-content .ProseMirror .sd-mention[data-type="mention"]::after {
          content: "";
        }
        .rte-content .ProseMirror .sd-intake-mention {
          display: inline-flex;
          align-items: center;
          gap: 0.25em;
          padding: 0 0.55em 0 0.45em;
          border-radius: 9999px;
          background: hsl(180 70% 55% / 0.15);
          color: hsl(180 70% 80%);
          border: 1px solid hsl(180 70% 55% / 0.35);
          font-weight: 500;
          font-size: 0.92em;
          white-space: nowrap;
          line-height: 1.4;
          vertical-align: baseline;
          cursor: pointer;
        }
        .rte-content .ProseMirror .sd-intake-mention:hover {
          background: hsl(180 70% 55% / 0.22);
          border-color: hsl(180 70% 55% / 0.5);
        }
        .rte-content .ProseMirror .sd-intake-mention::before {
          content: "";
          width: 0.9em;
          height: 0.9em;
          flex-shrink: 0;
          background-color: hsl(180 70% 80%);
          -webkit-mask: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect width='8' height='4' x='8' y='2' rx='1' ry='1'/><path d='M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2'/><path d='M12 11h4'/><path d='M12 16h4'/><path d='M8 11h.01'/><path d='M8 16h.01'/></svg>") no-repeat center / contain;
          mask: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect width='8' height='4' x='8' y='2' rx='1' ry='1'/><path d='M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2'/><path d='M12 11h4'/><path d='M12 16h4'/><path d='M8 11h.01'/><path d='M8 16h.01'/></svg>") no-repeat center / contain;
        }
        .rte-content .ProseMirror {
          min-height: var(--rte-min-height, 120px);
          cursor: text;
        }
        /* Cap only when --rte-max-height is set (editable surfaces). In
           read-only mode we unset the variable so the ticket-body renders
           at full height without creating a nested scroll region. */
        .rte-content.rte-capped .ProseMirror {
          max-height: var(--rte-max-height, 320px);
          overflow-y: auto;
        }
      `}</style>

      <div
        className={cn(
          "rounded-[var(--radius)] border border-glass bg-glass overflow-hidden",
          className
        )}
      >
        {editable && (
          <div className="flex items-center gap-1 px-3 py-2 border-b border-glass bg-glass flex-wrap">
            <ToolbarButton
              onClick={() => editor?.chain().focus().toggleBold().run()}
              active={editor?.isActive("bold")}
              title="Bold"
            >
              <Bold size={14} />
            </ToolbarButton>
            <ToolbarButton
              onClick={() => editor?.chain().focus().toggleItalic().run()}
              active={editor?.isActive("italic")}
              title="Italic"
            >
              <Italic size={14} />
            </ToolbarButton>

            <ToolbarSeparator />

            <ToolbarButton
              onClick={() => editor?.chain().focus().toggleHeading({ level: 2 }).run()}
              active={editor?.isActive("heading", { level: 2 })}
              title="Heading"
            >
              <Heading2 size={14} />
            </ToolbarButton>

            <ToolbarSeparator />

            <ToolbarButton
              onClick={() => editor?.chain().focus().toggleBulletList().run()}
              active={editor?.isActive("bulletList")}
              title="Bullet list"
            >
              <List size={14} />
            </ToolbarButton>
            <ToolbarButton
              onClick={() => editor?.chain().focus().toggleOrderedList().run()}
              active={editor?.isActive("orderedList")}
              title="Ordered list"
            >
              <ListOrdered size={14} />
            </ToolbarButton>

            <ToolbarSeparator />

            <ToolbarButton
              onClick={() => editor?.chain().focus().toggleCode().run()}
              active={editor?.isActive("code")}
              title="Inline code"
            >
              <Code size={14} />
            </ToolbarButton>
            <ToolbarButton
              onClick={setLink}
              active={editor?.isActive("link")}
              title="Link"
            >
              <LinkIcon size={14} />
            </ToolbarButton>

            {onUploadFile ? (
              <>
                <ToolbarSeparator />
                <ToolbarButton
                  onClick={triggerFilePicker}
                  title="Attach file"
                >
                  <Paperclip size={14} />
                </ToolbarButton>
              </>
            ) : null}
          </div>
        )}

        <EditorContent
          editor={editor}
          className={cn("rte-content px-4 py-3 text-sm", editable && "rte-capped")}
          style={{
            "--rte-min-height": minHeight,
            ...(editable ? { "--rte-max-height": maxHeight } : {}),
          } as React.CSSProperties}
        />
      </div>
    </>
  );
}

function collectFilesFromClipboard(dt: DataTransfer | null): File[] {
  if (!dt) return [];
  const out: File[] = [];
  // DataTransferItemList covers Chrome's "image copied from a webpage" path
  // (which is items, not files); plus regular .files for user-attached
  // copies from the OS clipboard / screenshots on Windows.
  if (dt.items && dt.items.length > 0) {
    for (let i = 0; i < dt.items.length; i++) {
      const item = dt.items[i];
      if (item.kind === "file") {
        const f = item.getAsFile();
        if (f) out.push(f);
      }
    }
  }
  if (out.length === 0 && dt.files && dt.files.length > 0) {
    out.push(...Array.from(dt.files));
  }
  return out;
}

function collectFilesFromDataTransfer(dt: DataTransfer | null): File[] {
  if (!dt) return [];
  if (dt.files && dt.files.length > 0) return Array.from(dt.files);
  return [];
}

async function handleFiles(
  files: File[],
  upload: (file: File) => Promise<TicketAttachmentMeta | null>,
  afterEach: (meta: TicketAttachmentMeta) => void,
): Promise<void> {
  // Sequential — the UX is clearer when chips appear one at a time, and
  // the server's blob-store is content-addressed so concurrent uploads of
  // the same image deduplicate anyway. Failures don't break the loop.
  for (const file of files) {
    const meta = await upload(file);
    if (meta) afterEach(meta);
  }
}

function insertUpload(
  editor: ReturnType<typeof useEditor> | null,
  meta: TicketAttachmentMeta,
  linkNonImage: boolean,
): void {
  if (!editor) return;
  if (meta.mimeType?.startsWith("image/")) {
    // setImage is provided by @tiptap/extension-image.
    // alt is the filename so screen readers + the timeline preview have a
    // meaningful label, even when the image fails to load.
    (editor.chain() as unknown as { focus: () => { setImage: (attrs: { src: string; alt: string }) => { run: () => void } } })
      .focus()
      .setImage({ src: meta.url, alt: meta.filename })
      .run();
    return;
  }
  // Non-image upload. Callers that own an attachment tray (tickets/mail) opt
  // out and render it themselves; document-only contexts (KB) insert a
  // clickable download link into the body so the file isn't orphaned. The
  // link mark + text node form sidesteps any HTML escaping concerns.
  if (!linkNonImage) return;
  editor
    .chain()
    .focus()
    .insertContent([
      {
        type: "text",
        text: meta.filename || "attachment",
        marks: [{ type: "link", attrs: { href: meta.url } }],
      },
      { type: "text", text: " " },
    ])
    .run();
}

/// Walk a Tiptap JSON doc and collect the `attrs.instanceId` of every
/// `intakeMention` node, in document order, deduplicated. Only nodes whose
/// draft-instance was successfully created (i.e. attrs.instanceId is a
/// string) are included — freshly-inserted chips waiting on the POST still
/// have `instanceId: null` and must not leak into LinkedFormIds.
export function extractIntakeInstanceIds(doc: unknown): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  const walk = (node: unknown) => {
    if (!node || typeof node !== "object") return;
    const n = node as {
      type?: string;
      attrs?: { instanceId?: string | null };
      content?: unknown[];
    };
    if (n.type === "intakeMention" && typeof n.attrs?.instanceId === "string") {
      const id = n.attrs.instanceId;
      if (!seen.has(id)) {
        seen.add(id);
        out.push(id);
      }
    }
    if (Array.isArray(n.content)) {
      for (const child of n.content) walk(child);
    }
  };
  walk(doc);
  return out;
}

/// Walk a Tiptap JSON doc and collect the `attrs.id` of every `mention`
/// node, in document order, deduplicated. Used at submit-time by the forms
/// to pass a concise `mentionedUserIds[]` to the server without re-parsing
/// the HTML string.
export function extractMentionIds(doc: unknown): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  const walk = (node: unknown) => {
    if (!node || typeof node !== "object") return;
    const n = node as { type?: string; attrs?: { id?: string }; content?: unknown[] };
    if (n.type === "mention" && typeof n.attrs?.id === "string") {
      const id = n.attrs.id;
      if (!seen.has(id)) {
        seen.add(id);
        out.push(id);
      }
    }
    if (Array.isArray(n.content)) {
      for (const child of n.content) walk(child);
    }
  };
  walk(doc);
  return out;
}

/// Split the flat mention-id list emitted by `onMentionsChange` into the two
/// server-side buckets. Tagging-mailbox ids are carried in the document with a
/// `mbox:` prefix (so the mention node needs no schema change); we strip it
/// here and route them to `mailboxIds`. Everything else is an agent user-id.
export function splitMentionIds(ids: string[]): {
  userIds: string[];
  mailboxIds: string[];
} {
  const userIds: string[] = [];
  const mailboxIds: string[] = [];
  for (const id of ids) {
    if (id.startsWith("mbox:")) mailboxIds.push(id.slice(5));
    else userIds.push(id);
  }
  return { userIds, mailboxIds };
}

/// Suggestion-plugin wiring for the `::` mention. The picker mixes two
/// kinds of items:
///   - "intake"   → insert an intake-form chip + async draft-instance create
///                  (legacy v0.0.19 flow; SendMailForm only)
///   - "template" → insert the template's sanitised HTML body inline at the
///                  trigger range, no chip, no roundtrip
///
/// The caller's `onIntakeQuery` decides which kinds to surface. Forms that
/// only want templates simply return `kind: "template"` items; the chip
/// pathway is dormant unless `onIntakeInsert` is also wired.
///
/// MentionNodeAttrs widened locally because we pass kind/bodyHtml through
/// the command props — Tiptap's own type only knows id/label.
type ComposeMentionProps = MentionNodeAttrs & {
  kind?: "intake" | "template" | "order" | "kb";
  bodyHtml?: string;
  href?: string;
};

function buildIntakeSuggestion(
  queryRef: React.MutableRefObject<
    ((query: string) => Promise<IntakeMentionItem[]>) | undefined
  >,
  insertRef: React.MutableRefObject<
    ((templateId: string) => Promise<string | null>) | undefined
  >,
  composeTokensRef: React.MutableRefObject<
    Record<string, string> | undefined
  >,
): Omit<SuggestionOptions<IntakeMentionItem, ComposeMentionProps>, "editor"> {
  return {
    char: "::",
    async items({ query }: { query: string }) {
      const fn = queryRef.current;
      if (!fn) return [];
      try {
        return await fn(query);
      } catch {
        return [];
      }
    },
    // Custom command — we ignore Tiptap's default insertContent for Mention
    // and dispatch on item kind. Templates get insertContent(html); intake
    // forms get the chip + async patch.
    command: ({ editor, range, props }: {
      editor: import("@tiptap/react").Editor;
      range: { from: number; to: number };
      props: ComposeMentionProps;
    }) => {
      const kind = props.kind ?? "intake";
      const id = (props.id as string | null) ?? "";
      const label = (props.label as string | null) ?? "";

      if (kind === "template") {
        // Strip the `::query` range, then drop the template body in.
        // Substitute every {{token}} that resolves to a non-empty value;
        // unresolved (empty / unknown) tokens keep their raw placeholder
        // so the agent can spot what still needs typing.
        const raw = props.bodyHtml ?? "";
        const rendered = substituteComposeTokens(raw, composeTokensRef.current);
        editor
          .chain()
          .focus()
          .insertContentAt(range, rendered)
          .run();
        return;
      }

      if (kind === "kb") {
        // v0.0.75 — insert a plain hyperlink to the article's public reader
        // page. A real <a> (not a pill) because this lands in outbound mail:
        // the customer's mail client must render it as a clickable link.
        const href = props.href ?? "";
        if (!href) return;
        editor
          .chain()
          .focus()
          .insertContentAt(range, [
            {
              type: "text",
              text: label || href,
              marks: [{ type: "link", attrs: { href } }],
            },
            { type: "text", text: " " },
          ])
          .run();
        return;
      }

      if (kind === "order") {
        // v0.0.59 — insert a clickable order pill (an orderMention node). The
        // pill carries data-order-id; clicking it (here, or in the rendered
        // note) opens the shared order-detail dialog via OrderPillHost.
        editor
          .chain()
          .focus()
          .insertContentAt(range, [
            { type: "orderMention", attrs: { id, label } },
            { type: "text", text: " " },
          ])
          .run();
        return;
      }

      editor
        .chain()
        .focus()
        .insertContentAt(range, [
          {
            type: "intakeMention",
            attrs: { id, label, instanceId: null },
          },
          { type: "text", text: " " },
        ])
        .run();

      const insert = insertRef.current;
      if (!insert || !id) return;

      // Fire-and-forget. On success, walk the doc to find the chip we
      // just inserted (matched by templateId + null instanceId) and
      // patch it with the real instance id. On failure, remove the chip.
      void insert(id)
        .then((instanceId) => {
          if (!instanceId) {
            removeChipByTemplateId(editor, id);
            return;
          }
          patchChipInstanceId(editor, id, instanceId);
        })
        .catch(() => {
          removeChipByTemplateId(editor, id);
        });
    },
    render: () => {
      let component: ReactRenderer<IntakeMentionListHandle> | null = null;
      let popup: TippyInstance | null = null;

      return {
        onStart: (props: SuggestionProps<IntakeMentionItem, ComposeMentionProps>) => {
          component = new ReactRenderer(IntakeMentionList, {
            props: {
              items: props.items,
              loading: false,
              command: (item: IntakeMentionItem) =>
                props.command({
                  id: item.id,
                  label: item.name,
                  kind: item.kind,
                  bodyHtml: item.bodyHtml,
                  href: item.href,
                } as ComposeMentionProps),
            },
            editor: props.editor,
          });

          if (!props.clientRect) return;

          popup = tippy(document.body, {
            getReferenceClientRect: () => props.clientRect!() ?? new DOMRect(),
            appendTo: () => document.body,
            content: component.element,
            showOnCreate: true,
            interactive: true,
            trigger: "manual",
            placement: "bottom-start",
            theme: "sd-mention",
          });
        },
        onUpdate(props: SuggestionProps<IntakeMentionItem, ComposeMentionProps>) {
          component?.updateProps({
            items: props.items,
            loading: false,
            command: (item: IntakeMentionItem) =>
              props.command({
                id: item.id,
                label: item.name,
                kind: item.kind,
                bodyHtml: item.bodyHtml,
                href: item.href,
              } as ComposeMentionProps),
          });
          if (!props.clientRect) return;
          popup?.setProps({
            getReferenceClientRect: () => props.clientRect!() ?? new DOMRect(),
          });
        },
        onKeyDown(props: { event: KeyboardEvent }) {
          if (props.event.key === "Escape") {
            popup?.hide();
            return true;
          }
          return component?.ref?.onKeyDown(props) ?? false;
        },
        onExit() {
          popup?.destroy();
          component?.destroy();
          popup = null;
          component = null;
        },
      };
    },
  };
}

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
// substituteComposeTokens / escapeHtml live in @/lib/composeTokens so the
// internal-note auto-insert path can reuse them without dragging the whole
// Tiptap editor module into its dependency graph.

function patchChipInstanceId(
  editor: import("@tiptap/react").Editor,
  templateId: string,
  instanceId: string,
): void {
  const tr = editor.state.tr;
  let changed = false;
  editor.state.doc.descendants((node, pos) => {
    if (
      node.type.name === "intakeMention" &&
      node.attrs.id === templateId &&
      !node.attrs.instanceId
    ) {
      tr.setNodeMarkup(pos, undefined, {
        ...node.attrs,
        instanceId,
      });
      changed = true;
    }
    return true;
  });
  if (changed) {
    editor.view.dispatch(tr);
  }
}

function removeChipByTemplateId(
  editor: import("@tiptap/react").Editor,
  templateId: string,
): void {
  const tr = editor.state.tr;
  const deletions: Array<{ from: number; to: number }> = [];
  editor.state.doc.descendants((node, pos) => {
    if (
      node.type.name === "intakeMention" &&
      node.attrs.id === templateId &&
      !node.attrs.instanceId
    ) {
      deletions.push({ from: pos, to: pos + node.nodeSize });
    }
    return true;
  });
  // Apply deletions from tail to head so earlier indexes stay valid.
  for (const d of deletions.reverse()) {
    tr.delete(d.from, d.to);
  }
  if (deletions.length > 0) editor.view.dispatch(tr);
}

/// Suggestion-plugin wiring for the Mention extension. The `char: "@@"`
/// trigger means the popover only appears after the user types two `@`s —
/// this sidesteps accidental activation on every email address that ends
/// up in a Reply/ReplyAll quoted body. The `items` function resolves the
/// current agent-search callback from the ref so the caller's debounced
/// query can update without recreating the editor.
function buildMentionSuggestion(
  queryRef: React.MutableRefObject<
    ((query: string) => Promise<MentionPickerItem[]>) | undefined
  >,
): Omit<SuggestionOptions<MentionPickerItem, MentionNodeAttrs>, "editor"> {
  return {
    char: "@@",
    async items({ query }: { query: string }) {
      const fn = queryRef.current;
      if (!fn) return [];
      try {
        return await fn(query);
      } catch {
        return [];
      }
    },
    render: () => {
      let component: ReactRenderer<MentionListHandle> | null = null;
      let popup: TippyInstance | null = null;

      // Mention's `command` callback expects `MentionNodeAttrs`
      // (`{ id: string | null; label: string | null }`); our MentionList
      // hands over concrete string values, so an identity-forwarder with
      // the looser signature satisfies both sides.
      const adapt = (
        command: SuggestionProps<MentionPickerItem, MentionNodeAttrs>["command"],
      ) => (attrs: { id: string; label: string }) => command(attrs);

      return {
        onStart: (props: SuggestionProps<MentionPickerItem, MentionNodeAttrs>) => {
          component = new ReactRenderer(MentionList, {
            props: {
              items: props.items,
              loading: false,
              command: adapt(props.command),
            },
            editor: props.editor,
          });

          if (!props.clientRect) return;

          popup = tippy(document.body, {
            getReferenceClientRect: () => props.clientRect!() ?? new DOMRect(),
            appendTo: () => document.body,
            content: component.element,
            showOnCreate: true,
            interactive: true,
            trigger: "manual",
            placement: "bottom-start",
            theme: "sd-mention",
          });
        },
        onUpdate(props: SuggestionProps<MentionPickerItem, MentionNodeAttrs>) {
          component?.updateProps({
            items: props.items,
            loading: false,
            command: adapt(props.command),
          });
          if (!props.clientRect) return;
          popup?.setProps({
            getReferenceClientRect: () => props.clientRect!() ?? new DOMRect(),
          });
        },
        onKeyDown(props: { event: KeyboardEvent }) {
          if (props.event.key === "Escape") {
            popup?.hide();
            return true;
          }
          return component?.ref?.onKeyDown(props) ?? false;
        },
        onExit() {
          popup?.destroy();
          component?.destroy();
          popup = null;
          component = null;
        },
      };
    },
  };
}
