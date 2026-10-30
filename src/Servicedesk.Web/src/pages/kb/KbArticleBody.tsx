import { useEffect, useState } from "react";
import { ListTree } from "lucide-react";
import { SafeHtml } from "@/components/SafeHtml";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogTitle,
} from "@/components/ui/dialog";
import { cn } from "@/lib/utils";
import type { KbHeading } from "@/lib/kbOutline";

/// Sanitized article body with the KB reader typography (`.kb-doc`: callouts,
/// tables, numbered step badges, framed screenshots) and click-to-zoom on
/// images. Pass the outline-processed html so headings carry their anchors.
export function KbArticleBody({ html }: { html: string }) {
  const [zoomed, setZoomed] = useState<{ src: string; alt: string } | null>(null);

  return (
    <>
      <div
        className="kb-doc"
        onClick={(e) => {
          const target = e.target as HTMLElement;
          // A linked image keeps its link behaviour.
          if (target.tagName !== "IMG" || target.closest("a")) return;
          const img = target as HTMLImageElement;
          setZoomed({ src: img.currentSrc || img.src, alt: img.alt });
        }}
      >
        <SafeHtml html={html} />
      </div>

      <Dialog open={zoomed !== null} onOpenChange={(open) => !open && setZoomed(null)}>
        <DialogContent className="w-auto max-w-[min(96vw,1400px)] gap-3 p-3 pt-11 sm:max-w-[min(96vw,1400px)]">
          <DialogTitle className="sr-only">Image</DialogTitle>
          {zoomed && (
            <>
              <img
                src={zoomed.src}
                alt={zoomed.alt}
                className="max-h-[82vh] w-auto max-w-full rounded-md object-contain"
              />
              {zoomed.alt ? (
                <DialogDescription className="px-1 pr-8 text-xs">{zoomed.alt}</DialogDescription>
              ) : (
                <DialogDescription className="sr-only">Enlarged image</DialogDescription>
              )}
            </>
          )}
        </DialogContent>
      </Dialog>
    </>
  );
}

/// "On this page" panel: the article's h2/h3 outline with the section in view
/// highlighted. Rendered only for articles long enough to need it.
export function KbOutlinePanel({ headings, className }: { headings: KbHeading[]; className?: string }) {
  const [activeId, setActiveId] = useState<string | null>(headings[0]?.id ?? null);

  useEffect(() => {
    const els = headings
      .map((h) => document.getElementById(h.id))
      .filter((el): el is HTMLElement => el !== null);
    if (els.length === 0) return;
    // Current section = the last heading that has scrolled past the top
    // band of the viewport. Listening in the capture phase catches the app's
    // inner scroll container as well as the window; one rAF per frame.
    let frame = 0;
    let scroller: Element | null = null;
    const update = () => {
      frame = 0;
      const band = window.innerHeight * 0.25;
      let current = els[0];
      for (const el of els) {
        if (el.getBoundingClientRect().top <= band) current = el;
        else break;
      }
      // At the very end the last sections can never reach the band; then the
      // last heading still on screen is the one being read.
      if (scroller && scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 4) {
        for (const el of els) {
          if (el.getBoundingClientRect().top < window.innerHeight) current = el;
        }
      }
      setActiveId(current.id);
    };
    const onScroll = (e: Event) => {
      const t = e.target instanceof Element ? e.target : document.scrollingElement;
      // Ignore other scroll areas (sidebar, dropdowns).
      if (t && t.contains(els[0])) scroller = t;
      if (!frame) frame = requestAnimationFrame(update);
    };
    update();
    window.addEventListener("scroll", onScroll, { capture: true, passive: true });
    return () => {
      window.removeEventListener("scroll", onScroll, { capture: true });
      if (frame) cancelAnimationFrame(frame);
    };
  }, [headings]);

  return (
    <nav aria-label="On this page" className={cn("glass-card flex flex-col gap-2 p-5", className)}>
      <div className="flex items-center gap-2 text-xs uppercase tracking-wider text-muted-foreground">
        <ListTree className="h-3.5 w-3.5" />
        On this page
      </div>
      <ul className="flex flex-col gap-0.5 border-l border-glass">
        {headings.map((h) => (
          <li key={h.id}>
            <button
              type="button"
              onClick={() => {
                document.getElementById(h.id)?.scrollIntoView({ behavior: "smooth", block: "start" });
                setActiveId(h.id);
              }}
              className={cn(
                "sd-kb-toc-link -ml-px block w-full border-l py-1 text-left text-xs leading-snug transition-colors",
                h.level === 3 ? "pl-6" : "pl-3",
                activeId === h.id
                  ? "border-primary font-medium text-foreground"
                  : "border-transparent text-muted-foreground hover:text-foreground",
              )}
            >
              {h.text}
            </button>
          </li>
        ))}
      </ul>
    </nav>
  );
}
