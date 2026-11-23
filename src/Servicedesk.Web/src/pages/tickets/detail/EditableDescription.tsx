import * as React from "react";
import { Pencil } from "lucide-react";
import { RichTextEditor } from "@/components/RichTextEditor";
import { stripBaseTags } from "@/lib/sanitize";

export function EditableDescription({
  html,
  text,
  onSave,
}: {
  html: string | null;
  text: string;
  onSave: (bodyHtml: string, bodyText: string) => Promise<void>;
}) {
  const [editing, setEditing] = React.useState(false);
  const [draftHtml, setDraftHtml] = React.useState(html ?? text);

  React.useEffect(() => {
    setDraftHtml(html ?? text);
  }, [html, text]);

  const save = async () => {
    // DOMParser: an inert document. A detached `div.innerHTML` is NOT inert —
    // it fetches <img> sources and fires their handlers — and draftHtml can
    // still be the raw inbound-mail body when saved unedited.
    const plainText =
      new DOMParser().parseFromString(stripBaseTags(draftHtml), "text/html").body.textContent ?? "";
    await onSave(draftHtml, plainText);
    setEditing(false);
  };

  const cancel = () => {
    setDraftHtml(html ?? text);
    setEditing(false);
  };

  const isEmpty = !html && !text.trim();

  if (!editing) {
    if (isEmpty) {
      return (
        <button
          type="button"
          onClick={() => setEditing(true)}
          className="w-full flex items-center gap-3 px-4 py-3 rounded-(--radius) border border-glass bg-glass text-muted-foreground/60 hover:bg-glass-hover hover:text-muted-foreground hover:border-glass-strong transition-colors text-sm"
        >
          <Pencil className="h-4 w-4 shrink-0" />
          Add a description...
        </button>
      );
    }

    return (
      <div
        className="group relative rounded-(--radius) border border-glass bg-glass px-4 py-3 cursor-pointer hover:bg-glass-hover hover:border-glass-strong transition-colors max-h-32 overflow-y-auto"
        onClick={() => setEditing(true)}
        title="Click to edit description"
      >
        <button
          type="button"
          className="absolute top-2 right-2 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-glass-hover transition-all z-10"
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
        <RichTextEditor
          content={html ?? text}
          editable={false}
          minHeight="0px"
          className="border-none bg-transparent rounded-none!"
        />
      </div>
    );
  }

  return (
    <div className="rounded-(--radius) border border-glass bg-glass p-4 space-y-3">
      <RichTextEditor
        content={draftHtml}
        onChange={setDraftHtml}
        placeholder="Describe the issue..."
        minHeight="100px"
      />
      <div className="flex items-center justify-between">
        <button
          type="button"
          onClick={cancel}
          className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-glass-hover transition-colors"
        >
          Cancel
        </button>
        <button
          type="button"
          onClick={save}
          className="px-3 py-1.5 text-xs rounded-md bg-primary text-white hover:bg-primary/90 transition-colors"
        >
          Save
        </button>
      </div>
    </div>
  );
}
