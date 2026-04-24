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
import type { TicketAttachmentMeta, AgentUser } from "@/lib/ticket-api";
import { MentionList, type MentionListHandle } from "./MentionList";
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
  /// When provided, the editor enables the @@-mention typeahead: typing `@@`
  /// opens a popover fed by this callback (debounced to ~120ms by the editor).
  /// Selecting a row inserts a Mention node whose `id` is the agent's user-id.
  /// Called alongside onChange on every update with the current set of
  /// mentioned user-ids (deduplicated, source order).
  onMentionQuery?: (query: string) => Promise<AgentUser[]>;
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
          ? "bg-white/10 text-white"
          : "text-muted-foreground hover:bg-white/10 hover:text-white"
      )}
    >
      {children}
    </button>
  );
}

function ToolbarSeparator() {
  return <div className="w-px h-4 bg-white/10 mx-1 shrink-0" />;
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
  onMentionQuery,
  onMentionsChange,
  onIntakeQuery,
  onIntakeInsert,
  onLinkedFormsChange,
  onIntakeChipClick,
}: RichTextEditorProps) {
  // Stash the latest upload callback in a ref so the editor extensions —
  // which see only the prop-snapshot at construction time — can still call
  // the freshest version after re-renders. Same trick keeps the editor from
  // being recreated when the parent's mutation state churns.
  const uploadRef = useRef<typeof onUploadFile>(onUploadFile);
  useEffect(() => {
    uploadRef.current = onUploadFile;
  }, [onUploadFile]);

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

  const extensions = [
    StarterKit,
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
      suggestion: buildIntakeSuggestion(intakeQueryRef, intakeInsertRef),
    });

    extensions.push(IntakeMention);
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
          insertImageIfApplicable(editorInstance(), meta),
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
          insertImageIfApplicable(editorInstance(), meta),
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
  // (e.g. after a save updates the query cache). Only for read-only editors
  // to avoid fighting with the user's cursor in editable mode.
  useEffect(() => {
    if (!editor || editable) return;
    const current = editor.getHTML();
    const incoming = content ?? "";
    if (current !== incoming) {
      editor.commands.setContent(incoming);
    }
  }, [editor, content, editable]);

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
        insertImageIfApplicable(editor, meta),
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
          list-style: disc;
        }
        .rte-content .ProseMirror ol {
          padding-left: 1.5em;
          list-style: decimal;
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
          "rounded-[var(--radius)] border border-white/10 bg-white/[0.04] overflow-hidden",
          className
        )}
      >
        {editable && (
          <div className="flex items-center gap-1 px-3 py-2 border-b border-white/10 bg-white/[0.02] flex-wrap">
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

function insertImageIfApplicable(
  editor: ReturnType<typeof useEditor> | null,
  meta: TicketAttachmentMeta,
): void {
  if (!editor) return;
  if (!meta.mimeType?.startsWith("image/")) return;
  // setImage is provided by @tiptap/extension-image.
  // alt is the filename so screen readers + the timeline preview have a
  // meaningful label, even when the image fails to load.
  (editor.chain() as unknown as { focus: () => { setImage: (attrs: { src: string; alt: string }) => { run: () => void } } })
    .focus()
    .setImage({ src: meta.url, alt: meta.filename })
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

/// Suggestion-plugin wiring for the `::` intake-form mention. Parallel
/// shape to `buildMentionSuggestion` but swaps the trigger char, query
/// source, and command callback. The command runs the caller's
/// `onIntakeInsert` to create a Draft on the server, then patches the
/// just-inserted node's `attrs.instanceId` once the id comes back. A
/// failed create removes the chip — the mail should never leave with a
/// placeholder whose draft was never committed.
function buildIntakeSuggestion(
  queryRef: React.MutableRefObject<
    ((query: string) => Promise<IntakeMentionItem[]>) | undefined
  >,
  insertRef: React.MutableRefObject<
    ((templateId: string) => Promise<string | null>) | undefined
  >,
): Omit<SuggestionOptions<IntakeMentionItem, MentionNodeAttrs>, "editor"> {
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
    // Wrap the default suggestion command so we can chase the
    // server roundtrip for draft-instance creation. Tiptap's
    // extension-mention calls our override; we run its default insert
    // first (so the chip is visible while the POST is in-flight) and
    // then patch the node when the id resolves.
    command: ({ editor, range, props }: {
      editor: import("@tiptap/react").Editor;
      range: { from: number; to: number };
      props: MentionNodeAttrs;
    }) => {
      const templateId = (props.id as string | null) ?? "";
      const label = (props.label as string | null) ?? "";

      editor
        .chain()
        .focus()
        .insertContentAt(range, [
          {
            type: "intakeMention",
            attrs: { id: templateId, label, instanceId: null },
          },
          { type: "text", text: " " },
        ])
        .run();

      const insert = insertRef.current;
      if (!insert || !templateId) return;

      // Fire-and-forget. On success, walk the doc to find the chip we
      // just inserted (matched by templateId + null instanceId) and
      // patch it with the real instance id. On failure, remove the chip.
      void insert(templateId)
        .then((instanceId) => {
          if (!instanceId) {
            removeChipByTemplateId(editor, templateId);
            return;
          }
          patchChipInstanceId(editor, templateId, instanceId);
        })
        .catch(() => {
          removeChipByTemplateId(editor, templateId);
        });
    },
    render: () => {
      let component: ReactRenderer<IntakeMentionListHandle> | null = null;
      let popup: TippyInstance | null = null;

      return {
        onStart: (props: SuggestionProps<IntakeMentionItem, MentionNodeAttrs>) => {
          component = new ReactRenderer(IntakeMentionList, {
            props: {
              items: props.items,
              loading: false,
              command: (attrs: { id: string; label: string }) =>
                props.command(attrs),
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
        onUpdate(props: SuggestionProps<IntakeMentionItem, MentionNodeAttrs>) {
          component?.updateProps({
            items: props.items,
            loading: false,
            command: (attrs: { id: string; label: string }) =>
              props.command(attrs),
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
    ((query: string) => Promise<AgentUser[]>) | undefined
  >,
): Omit<SuggestionOptions<AgentUser, MentionNodeAttrs>, "editor"> {
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
        command: SuggestionProps<AgentUser, MentionNodeAttrs>["command"],
      ) => (attrs: { id: string; label: string }) => command(attrs);

      return {
        onStart: (props: SuggestionProps<AgentUser, MentionNodeAttrs>) => {
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
        onUpdate(props: SuggestionProps<AgentUser, MentionNodeAttrs>) {
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
