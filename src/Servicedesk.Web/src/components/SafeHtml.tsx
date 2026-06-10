import { useMemo } from "react";
import DOMPurify from "dompurify";
import { cn } from "@/lib/utils";

type SafeHtmlProps = {
  html: string;
  className?: string;
};

/// Renders user-authored HTML (typically a sanitized Tiptap body) through a
/// second-pass DOMPurify defence-in-depth + a memoised
/// `dangerouslySetInnerHTML` wrapper. The wrapper object MUST be memoised
/// (not just the string) — re-creating `{ __html }` on every render makes
/// React reassign innerHTML, which causes embedded `<img>` elements to
/// re-fetch from the server every time the parent re-renders.
export function SafeHtml({ html, className }: SafeHtmlProps) {
  const wrapper = useMemo(() => {
    const clean = DOMPurify.sanitize(html ?? "", {
      USE_PROFILES: { html: true },
      // Strip everything the server-side sanitizer would also reject —
      // belt-and-braces. The server is the authoritative gate; this is just
      // a render-time safety net.
      FORBID_TAGS: ["script", "style", "iframe", "object", "embed", "form"],
      FORBID_ATTR: ["onerror", "onload", "onclick", "onmouseover", "onfocus"],
    });
    return { __html: clean as unknown as string };
  }, [html]);

  return (
    <div
      className={cn("kb-prose", className)}
      dangerouslySetInnerHTML={wrapper}
    />
  );
}
