import * as React from "react";
import {
  X,
  Loader2,
  AlertTriangle,
  FileText,
  Image as ImageIcon,
  FileArchive,
  File as FileIcon,
} from "lucide-react";
import { cn } from "@/lib/utils";
import type { AttachmentUploadItem } from "../hooks/useAttachmentUploads";

type Props = {
  items: AttachmentUploadItem[];
  onRemove: (localId: string) => void;
  className?: string;
};

function formatBytes(n: number): string {
  if (!Number.isFinite(n) || n < 0) return "";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function iconForMime(mime?: string): React.ComponentType<{ className?: string }> {
  if (!mime) return FileIcon;
  if (mime.startsWith("image/")) return ImageIcon;
  if (mime.startsWith("text/") || mime === "application/pdf") return FileText;
  if (mime.startsWith("application/zip") || mime.includes("archive") || mime.includes("compressed"))
    return FileArchive;
  return FileIcon;
}

export function AttachmentTray({ items, onRemove, className }: Props) {
  if (items.length === 0) return null;

  return (
    <div
      className={cn(
        "flex flex-wrap gap-1.5 pt-2",
        className,
      )}
      aria-label="Attachments"
    >
      {items.map((it) => {
        const Icon = iconForMime(it.meta?.mimeType);
        const sizeLabel = formatBytes(it.meta?.size ?? it.size);
        const isPending = it.status === "pending";
        const isFailed = it.status === "failed";
        return (
          <div
            key={it.localId}
            className={cn(
              "group inline-flex items-center gap-2 rounded-md border px-2 py-1 text-xs transition-colors",
              isFailed
                ? "border-red-500/30 bg-red-500/5 text-red-300"
                : isPending
                  ? "border-white/10 bg-white/[0.03] text-muted-foreground"
                  : "border-white/15 bg-white/[0.04] text-foreground/90",
            )}
            title={isFailed ? it.errorMessage : `${it.filename} · ${sizeLabel}`}
          >
            {isPending ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" />
            ) : isFailed ? (
              <AlertTriangle className="h-3.5 w-3.5 text-red-400" />
            ) : (
              <Icon className="h-3.5 w-3.5 text-primary" />
            )}
            <span className="max-w-[180px] truncate">{it.filename}</span>
            {!isFailed && (
              <span className="text-muted-foreground/70">{sizeLabel}</span>
            )}
            <button
              type="button"
              onClick={() => onRemove(it.localId)}
              className="ml-0.5 rounded p-0.5 text-muted-foreground/60 hover:bg-white/[0.06] hover:text-foreground transition-colors"
              title="Remove"
            >
              <X className="h-3 w-3" />
            </button>
          </div>
        );
      })}
    </div>
  );
}
