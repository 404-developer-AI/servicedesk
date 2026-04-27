import * as React from "react";
import { useEditor, EditorContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import Link from "@tiptap/extension-link";
import Placeholder from "@tiptap/extension-placeholder";
import { Bold, Italic, List, ListOrdered, Code, Link as LinkIcon } from "lucide-react";
import { TemplateVariablePicker } from "./TemplateVariablePicker";
import type { TriggerTemplateVariable } from "@/lib/api";
import { cn } from "@/lib/utils";

type Props = {
  variables: TriggerTemplateVariable[];
  /// Plain-text mode for subject + single-line fields. Keeps CR/LF out of
  /// the value (renderer's PlainText escape mode strips them anyway, but
  /// blocking at the input keeps the preview honest).
  mode: "plain" | "html";
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  className?: string;
};

/// Editor field used by every templating-aware trigger action (subject /
/// body_html / note bodies). Plain-text fields render a single-line input;
/// html fields render a Tiptap editor with the project's standard toolbar.
/// Both surfaces expose the "Insert variable" popover that prepends a
/// whitelisted `#{...}` placeholder at the cursor — same role as the spec's
/// `::` typeahead, just behind a button so it works equally well in plain
/// inputs (where typeahead-popover wiring is overkill).
export function TriggerTemplateField({
  variables,
  mode,
  value,
  onChange,
  placeholder,
  className,
}: Props) {
  if (mode === "plain") {
    return <PlainField {...{ variables, value, onChange, placeholder, className }} />;
  }
  return <HtmlField {...{ variables, value, onChange, placeholder, className }} />;
}

function PlainField({
  variables,
  value,
  onChange,
  placeholder,
  className,
}: {
  variables: TriggerTemplateVariable[];
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  className?: string;
}) {
  const inputRef = React.useRef<HTMLInputElement | null>(null);

  function insertAtCursor(snippet: string) {
    const el = inputRef.current;
    if (!el) {
      onChange((value ?? "") + snippet);
      return;
    }
    const start = el.selectionStart ?? value.length;
    const end = el.selectionEnd ?? value.length;
    const next = value.slice(0, start) + snippet + value.slice(end);
    onChange(next);
    requestAnimationFrame(() => {
      el.focus();
      const cursor = start + snippet.length;
      el.setSelectionRange(cursor, cursor);
    });
  }

  return (
    <div className={cn("space-y-1.5", className)}>
      <input
        ref={inputRef}
        type="text"
        value={value}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring focus:border-white/20"
      />
      <div className="flex justify-end">
        <TemplateVariablePicker variables={variables} onPick={insertAtCursor} />
      </div>
    </div>
  );
}

function HtmlField({
  variables,
  value,
  onChange,
  placeholder,
  className,
}: {
  variables: TriggerTemplateVariable[];
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  className?: string;
}) {
  const editor = useEditor({
    extensions: [
      StarterKit,
      Link.configure({ autolink: true, openOnClick: false }),
      Placeholder.configure({ placeholder: placeholder ?? "Body…" }),
    ],
    content: value || "",
    editable: true,
    onUpdate({ editor: e }) {
      onChange(e.getHTML());
    },
  });

  // Sync external value changes (e.g. action-list reorder) back into the
  // editor without fighting the user's cursor when they're typing.
  React.useEffect(() => {
    if (!editor) return;
    if (editor.isFocused) return;
    if (editor.getHTML() !== (value || "")) {
      editor.commands.setContent(value || "");
    }
  }, [editor, value]);

  function insertAtCursor(snippet: string) {
    if (!editor) return;
    editor.chain().focus().insertContent(snippet).run();
  }

  function setLink() {
    if (!editor) return;
    const prev = editor.getAttributes("link").href as string | undefined;
    const url = window.prompt("URL", prev ?? "");
    if (url === null) return;
    if (url === "") {
      editor.chain().focus().extendMarkRange("link").unsetLink().run();
      return;
    }
    editor.chain().focus().extendMarkRange("link").setLink({ href: url }).run();
  }

  return (
    <div className={cn("space-y-1.5", className)}>
      <div className="rounded-md border border-white/10 bg-white/[0.04] overflow-hidden">
        <div className="flex items-center gap-1 px-2 py-1.5 border-b border-white/10 bg-white/[0.02] flex-wrap">
          <ToolbarBtn
            onClick={() => editor?.chain().focus().toggleBold().run()}
            active={editor?.isActive("bold")}
            title="Bold"
          ><Bold size={13} /></ToolbarBtn>
          <ToolbarBtn
            onClick={() => editor?.chain().focus().toggleItalic().run()}
            active={editor?.isActive("italic")}
            title="Italic"
          ><Italic size={13} /></ToolbarBtn>
          <Sep />
          <ToolbarBtn
            onClick={() => editor?.chain().focus().toggleBulletList().run()}
            active={editor?.isActive("bulletList")}
            title="Bullet list"
          ><List size={13} /></ToolbarBtn>
          <ToolbarBtn
            onClick={() => editor?.chain().focus().toggleOrderedList().run()}
            active={editor?.isActive("orderedList")}
            title="Ordered list"
          ><ListOrdered size={13} /></ToolbarBtn>
          <Sep />
          <ToolbarBtn
            onClick={() => editor?.chain().focus().toggleCode().run()}
            active={editor?.isActive("code")}
            title="Inline code"
          ><Code size={13} /></ToolbarBtn>
          <ToolbarBtn
            onClick={setLink}
            active={editor?.isActive("link")}
            title="Link"
          ><LinkIcon size={13} /></ToolbarBtn>
          <div className="ml-auto">
            <TemplateVariablePicker variables={variables} onPick={insertAtCursor} />
          </div>
        </div>
        <EditorContent
          editor={editor}
          className="rte-content px-3 py-2.5 text-sm"
          style={{ "--rte-min-height": "120px" } as React.CSSProperties}
        />
      </div>
    </div>
  );
}

function ToolbarBtn({
  onClick,
  active,
  title,
  children,
}: {
  onClick: () => void;
  active?: boolean;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onMouseDown={(e) => { e.preventDefault(); onClick(); }}
      title={title}
      className={cn(
        "p-1.5 rounded-md transition-colors",
        active ? "bg-white/10 text-white" : "text-muted-foreground hover:bg-white/10 hover:text-white",
      )}
    >
      {children}
    </button>
  );
}

function Sep() {
  return <div className="w-px h-4 bg-white/10 mx-0.5 shrink-0" />;
}
