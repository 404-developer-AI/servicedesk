import * as React from "react";
import { sanitizeNoteHtml } from "@/lib/noteHtml";

/// The one renderer for a ticket event body (note, call, reply, mail): the
/// ticket timeline and the tag hover preview both use it, so a note reads
/// the same everywhere. Sanitising lives in lib/noteHtml.ts.

export function NoteBodyHtml({
  html,
  onImageClick,
  className,
}: {
  html: string;
  /// Called for a click on an inline image (the timeline opens its preview).
  onImageClick?: (img: HTMLImageElement, e: React.MouseEvent<HTMLDivElement>) => void;
  className?: string;
}) {
  const onClick = React.useCallback(
    (e: React.MouseEvent<HTMLDivElement>) => {
      if (!onImageClick) return;
      const target = e.target as HTMLElement;
      if (target.tagName !== "IMG") return;
      onImageClick(target as HTMLImageElement, e);
    },
    [onImageClick],
  );
  // Memoise both the sanitised output AND the wrapper object. The string
  // avoids re-running DOMPurify each render (its 3.4.x output can vary
  // byte-for-byte on identical input). The wrapper object matters even
  // more: React's reconciler compares `dangerouslySetInnerHTML` with
  // strict-equality on the prop value — `{__html: s}` as an inline
  // literal is a fresh object every render, so React calls setProp, and
  // setProp for dangerouslySetInnerHTML assigns `innerHTML` unconditionally
  // without re-comparing the string. That nukes every inline <img> from
  // the DOM each parent re-render (e.g. every second when useServerTime
  // ticks), which re-fires the GET — a sub-rosa loop that only shows up
  // without the HTTP cache (incognito, DevTools "Disable cache").
  const danger = React.useMemo(() => ({ __html: sanitizeNoteHtml(html) }), [html]);
  return (
    <div
      onClick={onClick}
      className={className ?? `rich-prose rich-prose-sm${onImageClick ? " [&_img]:cursor-zoom-in" : ""}`}
      dangerouslySetInnerHTML={danger}
    />
  );
}
