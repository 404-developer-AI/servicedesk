import * as React from "react";
import { highlightRanges } from "@/lib/ticketColumns";
import { SearchHighlightContext } from "./searchHighlightContext";

export function Highlight({ text }: { text: string }) {
  const tokens = React.useContext(SearchHighlightContext);
  const ranges = React.useMemo(() => highlightRanges(text, tokens), [text, tokens]);
  if (ranges.length === 0) return <>{text}</>;

  const parts: React.ReactNode[] = [];
  let at = 0;
  ranges.forEach(([s, e], i) => {
    if (s > at) parts.push(text.slice(at, s));
    parts.push(
      <mark key={i} className="sd-search-hit rounded-[3px] bg-primary/20 px-px text-inherit">
        {text.slice(s, e)}
      </mark>,
    );
    at = e;
  });
  if (at < text.length) parts.push(text.slice(at));
  return <>{parts}</>;
}
