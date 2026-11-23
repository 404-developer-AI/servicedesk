import * as React from "react";
import { Download } from "lucide-react";
import { canPreview } from "@/components/attachments/AttachmentPreviewDialog";
import { formatBytes } from "@/lib/format";
import { inlineUrl, usePreview } from "../timelinePreviewContext";
import type { MailAttachment } from "./attachmentMeta";

export function AttachmentChip({ attachment: a }: { attachment: MailAttachment }) {
  const preview = usePreview();
  const canPreviewFile = canPreview(a.mimeType);
  const handleClick = (e: React.MouseEvent) => {
    if (!canPreviewFile || !preview) return;
    e.preventDefault();
    preview.open({
      // open=true: a deliberate click on an attachment stays audit-logged,
      // unlike images embedded in a body (server skips only those).
      url: `${inlineUrl(a.url)}&open=true`,
      mimeType: a.mimeType,
      filename: a.name,
      sizeLabel: formatBytes(a.size),
      downloadUrl: a.url,
    });
  };
  return (
    <a
      href={a.url}
      target="_blank"
      rel="noreferrer"
      onClick={handleClick}
      className="inline-flex items-center gap-2 rounded-md border border-border/60 bg-background/40 px-2.5 py-1.5 text-xs text-foreground/90 hover:border-primary/50 hover:bg-primary/10"
      title={
        canPreviewFile
          ? `Preview · ${a.mimeType} · ${formatBytes(a.size)}`
          : `${a.mimeType} · ${formatBytes(a.size)}`
      }
    >
      <Download className="h-3.5 w-3.5 text-primary" />
      <span className="max-w-[220px] truncate">{a.name}</span>
      <span className="text-muted-foreground">{formatBytes(a.size)}</span>
    </a>
  );
}

export function PostAttachmentStrip({ attachments }: { attachments: MailAttachment[] }) {
  if (attachments.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-2 pt-1">
      {attachments.map((a) => (
        <AttachmentChip key={a.id} attachment={a} />
      ))}
    </div>
  );
}
