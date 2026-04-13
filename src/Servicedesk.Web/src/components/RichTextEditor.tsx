import { useEffect } from "react";
import { useEditor, EditorContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import Link from "@tiptap/extension-link";
import Placeholder from "@tiptap/extension-placeholder";
import {
  Bold,
  Italic,
  List,
  ListOrdered,
  Heading2,
  Code,
  Link as LinkIcon,
} from "lucide-react";
import { cn } from "@/lib/utils";

type RichTextEditorProps = {
  content?: string;
  onChange?: (html: string) => void;
  placeholder?: string;
  editable?: boolean;
  minHeight?: string;
  className?: string;
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
  className,
}: RichTextEditorProps) {
  const editor = useEditor({
    extensions: [
      StarterKit,
      Link.configure({
        autolink: true,
        openOnClick: false,
      }),
      Placeholder.configure({
        placeholder,
      }),
    ],
    content: content ?? "",
    editable,
    onUpdate({ editor: e }) {
      onChange?.(e.getHTML());
    },
  });

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
        .rte-content .ProseMirror {
          min-height: var(--rte-min-height, 120px);
          cursor: text;
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
          </div>
        )}

        <EditorContent
          editor={editor}
          className="rte-content px-4 py-3 text-sm"
          style={{ "--rte-min-height": minHeight } as React.CSSProperties}
        />
      </div>
    </>
  );
}
