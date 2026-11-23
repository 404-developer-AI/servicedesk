import * as React from "react";
import type { TabType } from "./composerTypes";

/// Scroll the bottom of the form flush with the viewport bottom so the
/// whole card (tabs + editor + Send/Add button) is visible after expand
/// or after switching to the taller "mail" tab. A single rAF lands
/// before Tiptap and SendMailForm finish hydrating their fields, so the
/// form grows afterwards and the Send button falls off-screen again;
/// the staggered follow-up passes catch those late reflows. "auto"
/// avoids a smooth-scroll being cancelled mid-way by the editor's
/// autofocus default-scroll.
export function useComposerScroll(
  formRef: React.RefObject<HTMLDivElement | null>,
  expanded: boolean,
  tab: TabType,
) {
  React.useEffect(() => {
    if (!expanded) return;
    const scrollToEnd = () => {
      formRef.current?.scrollIntoView({ behavior: "auto", block: "end" });
    };
    const raf = requestAnimationFrame(scrollToEnd);
    const timers = [80, 220].map((d) => window.setTimeout(scrollToEnd, d));
    return () => {
      cancelAnimationFrame(raf);
      timers.forEach((t) => window.clearTimeout(t));
    };
  }, [expanded, tab, formRef]);
}
