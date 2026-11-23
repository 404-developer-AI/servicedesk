import * as React from "react";
import { cn } from "@/lib/utils";
import { TimelineBodyHtml } from "../timelinePreview";

function SafeHtml({ html }: { html: string }) {
  return <TimelineBodyHtml html={html} />;
}

/// Renders a post body (HTML preferred, plain text fallback) with a
/// collapse affordance: caps visible height at COLLAPSED_MAX_PX, fades
/// the bottom edge into the background, and shows a Read more / Show
/// less button. Threshold includes a small slack so a body that just
/// barely exceeds the cap doesn't get a needless toggle. Re-measures
/// on body-change and when inline images finish loading (they can
/// significantly bump the rendered height after first paint).
// v0.1.27 — long threads: every mail body was sanitised, parsed and measured
// on the first render (#1532 on dev: 130 bodies of up to 100 KB, ~57k DOM
// nodes, 2.4 s main-thread task). Large bodies now mount when they come near
// the viewport, and the rest one per idle slice afterwards — so the first
// paint is fast and Ctrl+F still finds everything a moment later. Until then
// a placeholder keeps the exact collapsed footprint (cap + "Read more"), so
// nothing shifts when the real body replaces it.
const COLLAPSED_MAX_PX = 360;
const LAZY_BODY_MIN_CHARS = 8_000;
const LAZY_BODY_ROOT_MARGIN = "1200px 0px";

const idleMountQueue: Array<() => void> = [];
let idleMountScheduled = false;

function scheduleIdleMount(): void {
  if (idleMountScheduled || idleMountQueue.length === 0) return;
  idleMountScheduled = true;
  const run = () => {
    idleMountScheduled = false;
    // One body per slice: each mount is its own short React commit.
    idleMountQueue.shift()?.();
    scheduleIdleMount();
  };
  if (typeof window.requestIdleCallback === "function") {
    window.requestIdleCallback(run, { timeout: 2000 });
  } else {
    window.setTimeout(run, 16);
  }
}

function useDeferredBodyMount(enabled: boolean) {
  const ref = React.useRef<HTMLDivElement>(null);
  const [ready, setReady] = React.useState(!enabled);
  React.useEffect(() => {
    if (ready) return;
    let done = false;
    const mount = () => {
      if (done) return;
      done = true;
      setReady(true);
    };
    const el = ref.current;
    let io: IntersectionObserver | null = null;
    if (el && typeof IntersectionObserver === "function") {
      io = new IntersectionObserver(
        (entries) => {
          if (entries.some((e) => e.isIntersecting)) mount();
        },
        { rootMargin: LAZY_BODY_ROOT_MARGIN },
      );
      io.observe(el);
    }
    idleMountQueue.push(mount);
    scheduleIdleMount();
    return () => {
      done = true;
      io?.disconnect();
      const i = idleMountQueue.indexOf(mount);
      if (i >= 0) idleMountQueue.splice(i, 1);
    };
  }, [ready]);
  return { ref, ready, mountNow: () => setReady(true) };
}

export function CollapsibleBody({
  html,
  text,
}: {
  html: string | null | undefined;
  text: string | null | undefined;
}) {
  const deferred = useDeferredBodyMount((html?.length ?? 0) >= LAZY_BODY_MIN_CHARS);
  const [expandOnMount, setExpandOnMount] = React.useState(false);
  if (!deferred.ready) {
    return (
      <div ref={deferred.ref}>
        <div className="relative overflow-hidden" style={{ height: COLLAPSED_MAX_PX }}>
          <div className="pointer-events-none absolute inset-x-0 bottom-0 h-16 bg-linear-to-t from-background via-background/80 to-transparent" />
        </div>
        <button
          type="button"
          onClick={() => {
            setExpandOnMount(true);
            deferred.mountNow();
          }}
          className="mt-1 inline-flex items-center gap-1 text-xs font-medium text-primary hover:text-primary/80"
        >
          Read more…
        </button>
      </div>
    );
  }
  return <MountedBody html={html} text={text} initiallyExpanded={expandOnMount} />;
}

function MountedBody({
  html,
  text,
  initiallyExpanded,
}: {
  html: string | null | undefined;
  text: string | null | undefined;
  initiallyExpanded: boolean;
}) {
  const SLACK_PX = 16;
  const ref = React.useRef<HTMLDivElement>(null);
  const [expanded, setExpanded] = React.useState(initiallyExpanded);
  const [overflows, setOverflows] = React.useState(false);

  React.useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      setOverflows(el.scrollHeight > COLLAPSED_MAX_PX + SLACK_PX);
    };
    measure();
    // Re-measure once each image settles; scrollHeight before img.load
    // is 0-for-that-image, so the cap misfires on image-heavy bodies
    // without this.
    const imgs = Array.from(el.querySelectorAll("img"));
    const onImg = () => measure();
    imgs.forEach((img) => {
      if (!img.complete) {
        img.addEventListener("load", onImg);
        img.addEventListener("error", onImg);
      }
    });
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => {
      ro.disconnect();
      imgs.forEach((img) => {
        img.removeEventListener("load", onImg);
        img.removeEventListener("error", onImg);
      });
    };
  }, [html, text]);

  const hasBody = (html && html.length > 0) || (text && text.length > 0);
  if (!hasBody) return null;

  const collapsed = overflows && !expanded;
  return (
    <div>
      <div
        className={cn("relative", collapsed && "overflow-hidden")}
        style={collapsed ? { maxHeight: COLLAPSED_MAX_PX } : undefined}
      >
        <div ref={ref}>
          {html ? (
            <SafeHtml html={html} />
          ) : (
            <p className="whitespace-pre-wrap text-sm text-foreground/90">{text}</p>
          )}
        </div>
        {collapsed ? (
          <div className="pointer-events-none absolute inset-x-0 bottom-0 h-16 bg-linear-to-t from-background via-background/80 to-transparent" />
        ) : null}
      </div>
      {overflows ? (
        <button
          type="button"
          onClick={() => setExpanded((e) => !e)}
          className="mt-1 inline-flex items-center gap-1 text-xs font-medium text-primary hover:text-primary/80"
        >
          {expanded ? "Show less" : "Read more…"}
        </button>
      ) : null}
    </div>
  );
}
