import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { Search, X } from "lucide-react";
import { cn } from "@/lib/utils";

/// In-page search. Two modes in one bar:
///
///   • Filter  — the host page hides items that don't match the query
///     (list stays readable, only hits remain).
///   • Highlight — keeps everything visible, wraps matches in <mark>, and
///     lets Enter / F3 jump between hits with auto-scroll.
///
/// Ctrl+F within the host page opens the bar and suppresses the browser's
/// native find dialog. ESC closes it. Generic: the ticket detail page and
/// the KB section page mount this with their own placeholder + matching;
/// `matchesText` is the query predicate hosts use to implement Filter mode.

export type InPageSearchMode = "filter" | "highlight";

type Ctx = {
  open: () => void;
  close: () => void;
  isOpen: boolean;
  query: string;
  mode: InPageSearchMode;
  /** True when `haystack` contains the current query (or the query is empty). */
  matchesText: (haystack: string) => boolean;
  /** Register the DOM node that contains the searchable content so the
   *  highlighter knows where to scope its search. */
  registerScope: (el: HTMLElement | null) => void;
};

const InPageSearchContext = createContext<Ctx | null>(null);

export function useInPageSearch(): Ctx {
  const ctx = useContext(InPageSearchContext);
  if (!ctx) throw new Error("useInPageSearch must be used inside InPageSearchProvider");
  return ctx;
}

export function InPageSearchProvider({
  placeholder,
  children,
}: {
  /** Input placeholder, e.g. "Search in this ticket…". */
  placeholder: string;
  children: React.ReactNode;
}) {
  const [isOpen, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [mode, setMode] = useState<InPageSearchMode>("filter");
  const scopeRef = useRef<HTMLElement | null>(null);

  const open = useCallback(() => setOpen(true), []);
  const close = useCallback(() => {
    setOpen(false);
    setQuery("");
  }, []);

  const registerScope = useCallback((el: HTMLElement | null) => {
    scopeRef.current = el;
  }, []);

  // Ctrl+F / Cmd+F inside the host page opens the bar and cancels the
  // browser's native find dialog so we own the keystroke.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "f") {
        e.preventDefault();
        e.stopPropagation();
        setOpen(true);
      } else if (e.key === "Escape" && isOpen) {
        setOpen(false);
        setQuery("");
      }
    };
    window.addEventListener("keydown", handler, true);
    return () => window.removeEventListener("keydown", handler, true);
  }, [isOpen]);

  // DOM-level highlighting runs outside React. It walks the scope's text
  // nodes and wraps every match in a <mark>; unmounting/clearing the
  // query restores the original DOM so nothing is mutated permanently.
  useEffect(() => {
    const scope = scopeRef.current;
    if (!scope) return;
    removeHighlights(scope);
    if (mode !== "highlight" || !query.trim()) return;
    applyHighlights(scope, query.trim());
    return () => removeHighlights(scope);
  }, [mode, query, isOpen]);

  const matchesText = useCallback(
    (haystack: string) => {
      if (!query.trim()) return true;
      return haystack.toLowerCase().includes(query.trim().toLowerCase());
    },
    [query],
  );

  const value = useMemo<Ctx>(
    () => ({ open, close, isOpen, query, mode, matchesText, registerScope }),
    [open, close, isOpen, query, mode, matchesText, registerScope],
  );

  return (
    <InPageSearchContext.Provider value={value}>
      {children}
      {isOpen && (
        <SearchBar
          query={query}
          setQuery={setQuery}
          mode={mode}
          setMode={setMode}
          close={close}
          placeholder={placeholder}
        />
      )}
    </InPageSearchContext.Provider>
  );
}

function SearchBar({
  query,
  setQuery,
  mode,
  setMode,
  close,
  placeholder,
}: {
  query: string;
  setQuery: (v: string) => void;
  mode: InPageSearchMode;
  setMode: (m: InPageSearchMode) => void;
  close: () => void;
  placeholder: string;
}) {
  const inputRef = useRef<HTMLInputElement | null>(null);
  const [hitIndex, setHitIndex] = useState(0);
  const [hitCount, setHitCount] = useState(0);

  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  // In highlight mode, count <mark> nodes and jump between them.
  // Uses requestAnimationFrame so the parent provider's effect (which
  // applies the <mark> nodes) has finished before we count them.
  useEffect(() => {
    if (mode !== "highlight") {
      setHitCount(0);
      setHitIndex(0);
      return;
    }
    const raf = requestAnimationFrame(() => {
      const marks = document.querySelectorAll("mark[data-itsearch-hit]");
      setHitCount(marks.length);
      setHitIndex(marks.length > 0 ? 1 : 0);
      if (marks.length > 0) {
        (marks[0] as HTMLElement).scrollIntoView({ block: "center", behavior: "smooth" });
        marks.forEach((m, i) => m.classList.toggle("itsearch-current", i === 0));
      }
    });
    return () => cancelAnimationFrame(raf);
  }, [mode, query]);

  function jump(delta: number) {
    const marks = document.querySelectorAll("mark[data-itsearch-hit]");
    if (marks.length === 0) return;
    const next = ((hitIndex - 1 + delta + marks.length) % marks.length) + 1;
    setHitIndex(next);
    marks.forEach((m, i) => m.classList.toggle("itsearch-current", i === next - 1));
    (marks[next - 1] as HTMLElement).scrollIntoView({ block: "center", behavior: "smooth" });
  }

  return (
    <div
      className="fixed top-6 left-1/2 z-50 w-[min(640px,90vw)] -translate-x-1/2 rounded-xl border border-glass bg-background/95 p-2 shadow-2xl backdrop-blur ring-1 ring-inset ring-white/5"
      role="dialog"
      aria-label={placeholder}
    >
      <div className="flex items-center gap-2">
        <Search className="h-4 w-4 text-muted-foreground" />
        <input
          ref={inputRef}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              if (mode === "highlight") jump(e.shiftKey ? -1 : 1);
            } else if (e.key === "F3") {
              e.preventDefault();
              if (mode === "highlight") jump(e.shiftKey ? -1 : 1);
            }
          }}
          placeholder={placeholder}
          className="flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
        />

        <div className="flex items-center rounded-md border border-glass bg-glass p-0.5 text-xs">
          <ToggleButton active={mode === "filter"} onClick={() => setMode("filter")}>
            Filter
          </ToggleButton>
          <ToggleButton active={mode === "highlight"} onClick={() => setMode("highlight")}>
            Highlight
          </ToggleButton>
        </div>

        {mode === "highlight" && (
          <div className="flex items-center gap-1 text-xs text-muted-foreground">
            <span>
              {hitCount === 0 ? "0 hits" : `${hitIndex}/${hitCount}`}
            </span>
            <button
              onClick={() => jump(-1)}
              className="rounded px-1 hover:bg-glass-hover"
              aria-label="Previous match"
            >
              ↑
            </button>
            <button
              onClick={() => jump(1)}
              className="rounded px-1 hover:bg-glass-hover"
              aria-label="Next match"
            >
              ↓
            </button>
          </div>
        )}

        <button
          onClick={close}
          className="rounded p-1 text-muted-foreground hover:bg-glass-hover"
          aria-label="Close"
        >
          <X className="h-4 w-4" />
        </button>
      </div>
    </div>
  );
}

function ToggleButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      className={cn(
        "rounded px-2 py-1 transition-colors",
        active ? "bg-glass-strong text-foreground" : "text-muted-foreground hover:text-foreground",
      )}
    >
      {children}
    </button>
  );
}

// ─── DOM highlight helpers (scoped, reversible) ─────────────────────────

const HIGHLIGHT_ATTR = "data-itsearch-hit";

function applyHighlights(scope: HTMLElement, needle: string) {
  const lower = needle.toLowerCase();
  if (!lower) return;

  const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT, {
    acceptNode(node) {
      const parent = node.parentElement;
      if (!parent) return NodeFilter.FILTER_REJECT;
      if (parent.closest("mark[" + HIGHLIGHT_ATTR + "]")) return NodeFilter.FILTER_REJECT;
      if (parent.tagName === "SCRIPT" || parent.tagName === "STYLE") return NodeFilter.FILTER_REJECT;
      return node.nodeValue && node.nodeValue.toLowerCase().includes(lower)
        ? NodeFilter.FILTER_ACCEPT
        : NodeFilter.FILTER_REJECT;
    },
  });

  const victims: Text[] = [];
  let n: Node | null = walker.nextNode();
  while (n) {
    victims.push(n as Text);
    n = walker.nextNode();
  }

  for (const textNode of victims) {
    const text = textNode.nodeValue ?? "";
    const lowerText = text.toLowerCase();
    const fragment = document.createDocumentFragment();
    let i = 0;
    while (i < text.length) {
      const at = lowerText.indexOf(lower, i);
      if (at === -1) {
        fragment.appendChild(document.createTextNode(text.slice(i)));
        break;
      }
      if (at > i) fragment.appendChild(document.createTextNode(text.slice(i, at)));
      const mark = document.createElement("mark");
      mark.setAttribute(HIGHLIGHT_ATTR, "1");
      mark.textContent = text.slice(at, at + lower.length);
      fragment.appendChild(mark);
      i = at + lower.length;
    }
    textNode.parentNode?.replaceChild(fragment, textNode);
  }
}

function removeHighlights(scope: HTMLElement) {
  const marks = scope.querySelectorAll(`mark[${HIGHLIGHT_ATTR}]`);
  marks.forEach((m) => {
    const parent = m.parentNode;
    if (!parent) return;
    while (m.firstChild) parent.insertBefore(m.firstChild, m);
    parent.removeChild(m);
    parent.normalize();
  });
}
