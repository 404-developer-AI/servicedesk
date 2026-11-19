import * as React from "react";
import type { AttachmentPreview } from "@/components/attachments/AttachmentPreviewDialog";

/// The ticket timeline's shared attachment preview: one dialog per
/// timeline, opened from attachment chips and from inline images in any
/// body rendered under it (notes, mails, calls and — v0.1.58 — note replies).

export type PreviewContextValue = {
  open: (preview: AttachmentPreview) => void;
};

export const PreviewContext = React.createContext<PreviewContextValue | null>(null);

export function usePreview() {
  return React.useContext(PreviewContext);
}

export function inlineUrl(url: string): string {
  return url.includes("?") ? `${url}&inline=true` : `${url}?inline=true`;
}
