import { useRef, useState, type ReactNode } from "react";
import { Paperclip, X, FileText, Image as ImageIcon, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { RichTextEditor } from "@/components/RichTextEditor";
import { formatBytes } from "@/portal/portalShared";
import { cn } from "@/lib/utils";

export type PendingFile = { localId: string; file: File };

type Props = {
  value: string;
  onChange: (html: string) => void;
  files: PendingFile[];
  onFilesChange: (files: PendingFile[]) => void;
  placeholder?: string;
  minHeight?: string;
  disabled?: boolean;
  /// Progress label while the parent is posting + uploading.
  busyLabel?: string | null;
  submitLabel: string;
  onSubmit: () => void;
  extra?: ReactNode;
  autoFocus?: boolean;
};

function newLocalId() {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `local-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

/// Reduced composer for customers: the shared Tiptap editor without any
/// agent affordances (mentions, templates, signatures, inline image
/// uploads) + a file tray. Files are uploaded AFTER the message is posted,
/// onto that message — so this component only collects them.
export function PortalComposer({
  value,
  onChange,
  files,
  onFilesChange,
  placeholder,
  minHeight = "160px",
  disabled,
  busyLabel,
  submitLabel,
  onSubmit,
  extra,
  autoFocus,
}: Props) {
  const inputRef = useRef<HTMLInputElement | null>(null);
  const [dragOver, setDragOver] = useState(false);

  function addFiles(list: FileList | File[]) {
    const next = Array.from(list)
      .filter((f) => f.size > 0)
      .map((file) => ({ localId: newLocalId(), file }));
    if (next.length) onFilesChange([...files, ...next]);
  }

  return (
    <div
      className={cn("space-y-3", dragOver && "ring-2 ring-primary/40 rounded-[var(--radius)]")}
      onDragOver={(e) => {
        e.preventDefault();
        if (!disabled) setDragOver(true);
      }}
      onDragLeave={() => setDragOver(false)}
      onDrop={(e) => {
        e.preventDefault();
        setDragOver(false);
        if (!disabled && e.dataTransfer.files.length) addFiles(e.dataTransfer.files);
      }}
    >
      <RichTextEditor
        content={value}
        onChange={onChange}
        placeholder={placeholder}
        editable={!disabled}
        minHeight={minHeight}
        maxHeight="420px"
        autoFocus={autoFocus}
      />
      {files.length > 0 && (
        <ul className="flex flex-wrap gap-2" data-testid="portal-file-tray">
          {files.map((f) => (
            <li
              key={f.localId}
              className="inline-flex max-w-full items-center gap-2 rounded-md border border-glass bg-glass px-2.5 py-1.5 text-xs"
            >
              {f.file.type.startsWith("image/") ? <ImageIcon className="h-3.5 w-3.5 text-muted-foreground" /> : <FileText className="h-3.5 w-3.5 text-muted-foreground" />}
              <span className="truncate">{f.file.name}</span>
              <span className="text-muted-foreground">{formatBytes(f.file.size)}</span>
              {!disabled && (
                <button
                  type="button"
                  className="ml-1 text-muted-foreground hover:text-foreground"
                  aria-label={`Remove ${f.file.name}`}
                  onClick={() => onFilesChange(files.filter((x) => x.localId !== f.localId))}
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <input
          ref={inputRef}
          type="file"
          multiple
          className="hidden"
          onChange={(e) => {
            if (e.target.files) addFiles(e.target.files);
            e.target.value = "";
          }}
        />
        <Button type="button" variant="outline" size="sm" className="gap-1.5" disabled={disabled} onClick={() => inputRef.current?.click()}>
          <Paperclip className="h-3.5 w-3.5" />
          Attach files
        </Button>
        {extra}
        <div className="ml-auto flex items-center gap-3">
          {busyLabel ? (
            <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
              {busyLabel}
            </span>
          ) : null}
          <Button type="button" size="sm" disabled={disabled} onClick={onSubmit}>
            {submitLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}

/// Strips tags to decide whether the editor holds anything but whitespace.
export function htmlHasText(html: string): boolean {
  return html.replace(/<[^>]*>/g, "").replace(/&nbsp;/g, " ").trim().length > 0;
}
