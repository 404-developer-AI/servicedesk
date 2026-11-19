import * as React from "react";
import { NoteBodyHtml } from "@/components/NoteBodyHtml";
import { inlineUrl, usePreview } from "./timelinePreviewContext";

/// A timeline body: clicking an inline image opens it full size in the
/// timeline's preview dialog (outside a timeline it renders as plain HTML).
export function TimelineBodyHtml({ html, className }: { html: string; className?: string }) {
  const preview = usePreview();
  const onImageClick = React.useCallback(
    (img: HTMLImageElement, e: React.MouseEvent<HTMLDivElement>) => {
      if (!preview) return;
      const src = img.currentSrc || img.src;
      if (!src) return;
      e.preventDefault();
      // Inline images served by the attachment endpoints accept the
      // `inline=true` hint; external images pass through untouched.
      const url = src.includes("/api/tickets/") ? inlineUrl(src) : src;
      preview.open({
        url,
        mimeType: "image/*",
        filename: img.alt || "Image",
      });
    },
    [preview],
  );
  return (
    <NoteBodyHtml
      html={html}
      onImageClick={preview ? onImageClick : undefined}
      className={className
        ? `${className}${preview ? " [&_img]:cursor-zoom-in" : ""}`
        : undefined}
    />
  );
}
