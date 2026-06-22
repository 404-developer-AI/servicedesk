import * as React from "react";
import { AnimatePresence, motion } from "framer-motion";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { BookOpen, MessageCircleQuestion, Search, Send, Sparkles, X } from "lucide-react";
import {
  apiErrorMessage,
  kbChatApi,
  preferencesApi,
  type KbChatCitation,
  type KbChatHistoryMessage,
} from "@/lib/api";
import { useAuth } from "@/auth/authStore";
import { SafeHtml } from "@/components/SafeHtml";
import { cn } from "@/lib/utils";

/// Persisted button position (top-left, viewport pixels). Snapped to the
/// nearest edge after every drag, so it always hugs a browser edge.
const POS_PREF_KEY = "workspace:aiChatButton";

const BTN = 56; // button diameter (px) — keep in sync with h-14 w-14
const MARGIN = 24; // gap kept from the viewport edge

type Pos = { x: number; y: number };

type ChatMessage = {
  id: number;
  role: "user" | "assistant";
  text: string;
  html?: string;
  citations?: KbChatCitation[];
  error?: boolean;
};

function clampPos(p: Pos): Pos {
  const maxX = Math.max(MARGIN, window.innerWidth - BTN - MARGIN);
  const maxY = Math.max(MARGIN, window.innerHeight - BTN - MARGIN);
  return {
    x: Math.min(Math.max(p.x, MARGIN), maxX),
    y: Math.min(Math.max(p.y, MARGIN), maxY),
  };
}

function defaultPos(): Pos {
  return clampPos({ x: window.innerWidth, y: window.innerHeight });
}

/// Snap to whichever edge the button is closest to, keeping the orthogonal
/// coordinate. Mirrors the "drag it around the outside edges" behaviour.
function snapToEdge(p: Pos): Pos {
  const left = p.x;
  const right = window.innerWidth - (p.x + BTN);
  const top = p.y;
  const bottom = window.innerHeight - (p.y + BTN);
  const min = Math.min(left, right, top, bottom);
  const snapped = { ...p };
  if (min === left) snapped.x = MARGIN;
  else if (min === right) snapped.x = window.innerWidth - BTN - MARGIN;
  else if (min === top) snapped.y = MARGIN;
  else snapped.y = window.innerHeight - BTN - MARGIN;
  return clampPos(snapped);
}

/// Floating, draggable knowledge-base chat. Agent/Admin only, and only when
/// the feature is enabled, configured (key + ZDR) and the agent has KB access.
/// Mounted once at AppShell level so it follows the agent across every page.
export function KbChatWidget() {
  const { user } = useAuth();
  const isAgent = user?.role === "Agent" || user?.role === "Admin";
  const eligible = isAgent && !!user?.kbEnabled;

  // Cheap, cached gate query — only when the user could ever see the button.
  const statusQ = useQuery({
    queryKey: ["kb-chat", "status"],
    queryFn: kbChatApi.status,
    enabled: eligible,
    staleTime: 5 * 60_000,
  });

  if (!eligible || !statusQ.data?.ready) return null;
  return <ChatLauncher />;
}

function ChatLauncher() {
  // Start at the default corner so the button is always visible immediately,
  // even if the saved-position fetch is slow or fails.
  const [pos, setPos] = React.useState<Pos>(() => defaultPos());
  const posRef = React.useRef<Pos>(pos);
  const [open, setOpen] = React.useState(false);
  // Once the saved position is applied (or the agent drags), a late-arriving
  // fetch must not clobber the current position.
  const settled = React.useRef(false);

  const setPosBoth = React.useCallback((p: Pos) => {
    posRef.current = p;
    setPos(p);
  }, []);

  // Apply the saved position once, when it loads.
  const savedQ = useQuery({
    queryKey: ["preferences", "workspace"],
    queryFn: () => preferencesApi.getWorkspace(),
    staleTime: 60_000,
  });
  React.useEffect(() => {
    if (settled.current || !savedQ.data) return;
    const raw = savedQ.data[POS_PREF_KEY];
    if (raw) {
      try {
        const parsed = JSON.parse(raw) as Pos;
        if (typeof parsed.x === "number" && typeof parsed.y === "number") {
          setPosBoth(clampPos(parsed));
        }
      } catch {
        // keep the default
      }
    }
    settled.current = true;
  }, [savedQ.data, setPosBoth]);

  // Keep the button on-screen when the viewport changes size.
  React.useEffect(() => {
    const onResize = () => {
      if (posRef.current) setPosBoth(clampPos(posRef.current));
    };
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, [setPosBoth]);

  const drag = React.useRef<{ sx: number; sy: number; ox: number; oy: number; moved: boolean } | null>(null);

  const onPointerDown = (e: React.PointerEvent<HTMLButtonElement>) => {
    if (!posRef.current) return;
    (e.target as HTMLElement).setPointerCapture(e.pointerId);
    drag.current = { sx: e.clientX, sy: e.clientY, ox: posRef.current.x, oy: posRef.current.y, moved: false };
  };

  const onPointerMove = (e: React.PointerEvent<HTMLButtonElement>) => {
    const d = drag.current;
    if (!d) return;
    const dx = e.clientX - d.sx;
    const dy = e.clientY - d.sy;
    if (Math.abs(dx) > 4 || Math.abs(dy) > 4) d.moved = true;
    setPosBoth(clampPos({ x: d.ox + dx, y: d.oy + dy }));
  };

  const onPointerUp = (e: React.PointerEvent<HTMLButtonElement>) => {
    const d = drag.current;
    if (!d) return;
    drag.current = null;
    try {
      (e.target as HTMLElement).releasePointerCapture(e.pointerId);
    } catch {
      // capture may already be gone
    }
    if (!d.moved) {
      setOpen((o) => !o);
      return;
    }
    settled.current = true; // a manual move wins over any late-loading saved value
    const snapped = snapToEdge(posRef.current);
    setPosBoth(snapped);
    preferencesApi.fireAndForgetWorkspaceSave([
      { key: POS_PREF_KEY, value: JSON.stringify(snapped) },
    ]);
  };

  // Open the panel into the quadrant the button sits in, clamped to the
  // viewport so it never spills off-screen regardless of the edge.
  const centerX = pos.x + BTN / 2;
  const centerY = pos.y + BTN / 2;
  const onRight = centerX > window.innerWidth / 2;
  const onBottom = centerY > window.innerHeight / 2;
  const panelStyle: React.CSSProperties = {
    [onRight ? "right" : "left"]: MARGIN,
    [onBottom ? "bottom" : "top"]: BTN + MARGIN + 12,
  };

  return (
    <>
      <AnimatePresence>
        {open && (
          <ChatPanel
            key="kb-chat-panel"
            style={panelStyle}
            origin={`${onBottom ? "bottom" : "top"} ${onRight ? "right" : "left"}`}
            onClose={() => setOpen(false)}
          />
        )}
      </AnimatePresence>

      <button
        type="button"
        aria-label="Knowledge-base assistant"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        style={{ left: pos.x, top: pos.y }}
        className={cn(
          "fixed z-[58] flex h-14 w-14 touch-none select-none items-center justify-center rounded-full",
          "border border-glass-strong shadow-2xl backdrop-blur-xl",
          "bg-gradient-to-br from-violet-500/90 to-indigo-600/90 text-white",
          "transition-transform hover:scale-105 active:scale-95",
          open && "ring-2 ring-violet-400/50",
        )}
      >
        {open ? <X className="h-6 w-6" /> : <MessageCircleQuestion className="h-6 w-6" />}
      </button>
    </>
  );
}

let nextId = 1;

function ChatPanel({
  style,
  origin,
  onClose,
}: {
  style: React.CSSProperties;
  origin: string;
  onClose: () => void;
}) {
  const [messages, setMessages] = React.useState<ChatMessage[]>([]);
  const [input, setInput] = React.useState("");
  const [sending, setSending] = React.useState(false);
  const scrollRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" });
  }, [messages, sending]);

  const send = async () => {
    const text = input.trim();
    if (text.length === 0 || sending) return;

    const history: KbChatHistoryMessage[] = messages
      .filter((m) => !m.error)
      .map((m) => ({ role: m.role, text: m.text }));

    const userMsg: ChatMessage = { id: nextId++, role: "user", text };
    setMessages((prev) => [...prev, userMsg]);
    setInput("");
    setSending(true);

    try {
      const res = await kbChatApi.send(history, text);
      setMessages((prev) => [
        ...prev,
        {
          id: nextId++,
          role: "assistant",
          text: res.reply,
          html: res.replyHtml,
          citations: res.citations,
        },
      ]);
    } catch (err) {
      const msg = apiErrorMessage(err) ?? "Something went wrong reaching the assistant.";
      setMessages((prev) => [
        ...prev,
        { id: nextId++, role: "assistant", text: msg, error: true },
      ]);
    } finally {
      setSending(false);
    }
  };

  return (
    <motion.div
      initial={{ opacity: 0, scale: 0.94, y: 8 }}
      animate={{ opacity: 1, scale: 1, y: 0 }}
      exit={{ opacity: 0, scale: 0.96, y: 8 }}
      transition={{ type: "spring", stiffness: 320, damping: 28 }}
      style={{ ...style, transformOrigin: origin }}
      className={cn(
        "fixed z-[59] flex max-h-[70vh] w-[380px] max-w-[calc(100vw-2rem)] flex-col overflow-hidden",
        "rounded-2xl border border-glass-strong shadow-2xl",
        "bg-popover/95 backdrop-blur-xl",
      )}
      role="dialog"
      aria-label="Knowledge-base assistant"
    >
      {/* Gradient strip — keeps it from feeling generic-AI-default. */}
      <div className="h-1 w-full bg-gradient-to-r from-violet-500 via-indigo-500 to-blue-500" />

      <header className="flex items-center justify-between gap-3 px-4 py-3">
        <div className="flex items-center gap-2">
          <span className="flex h-7 w-7 items-center justify-center rounded-full border border-glass-strong bg-violet-500/15 text-violet-300">
            <Sparkles className="h-4 w-4" />
          </span>
          <div className="min-w-0">
            <p className="text-sm font-semibold text-foreground">Knowledge base</p>
            <p className="text-[10px] uppercase tracking-wider text-muted-foreground/60">
              Searches your KB only
            </p>
          </div>
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close"
          className="rounded-md p-1 text-muted-foreground/60 transition-colors hover:bg-glass-hover hover:text-foreground"
        >
          <X className="h-4 w-4" />
        </button>
      </header>

      <div ref={scrollRef} className="min-h-[140px] flex-1 space-y-3 overflow-y-auto px-4 py-2">
        {messages.length === 0 ? (
          <div className="flex h-full flex-col items-center justify-center gap-2 py-8 text-center">
            <BookOpen className="h-8 w-8 text-muted-foreground/40" />
            <p className="text-sm text-muted-foreground">
              Ask about anything in the knowledge base.
            </p>
            <p className="max-w-[16rem] text-xs text-muted-foreground/60">
              e.g. “How do I install the Sophos VPN?” — I’ll point you to the right article.
            </p>
          </div>
        ) : (
          messages.map((m) => <MessageBubble key={m.id} message={m} />)
        )}
        {sending && (
          <div className="flex items-center gap-2 text-xs text-muted-foreground/70">
            <Search className="h-3.5 w-3.5 animate-pulse" />
            Searching the knowledge base…
          </div>
        )}
      </div>

      <footer className="border-t border-glass p-3">
        <div className="flex items-end gap-2">
          <textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                void send();
              }
            }}
            placeholder="Ask a question…"
            rows={1}
            className={cn(
              "max-h-28 min-h-[2.25rem] flex-1 resize-none rounded-lg border border-glass bg-glass px-3 py-2 text-sm",
              "text-foreground placeholder:text-muted-foreground/50 focus:outline-none focus:ring-1 focus:ring-violet-400/40",
            )}
          />
          <button
            type="button"
            onClick={() => void send()}
            disabled={input.trim().length === 0 || sending}
            aria-label="Send"
            className={cn(
              "flex h-9 w-9 shrink-0 items-center justify-center rounded-lg transition-colors",
              "bg-violet-500/90 text-white hover:bg-violet-400 disabled:opacity-40",
            )}
          >
            <Send className="h-4 w-4" />
          </button>
        </div>
      </footer>
    </motion.div>
  );
}

function MessageBubble({ message }: { message: ChatMessage }) {
  if (message.role === "user") {
    return (
      <div className="flex justify-end">
        <div className="max-w-[85%] rounded-2xl rounded-br-sm bg-violet-500/20 px-3 py-2 text-sm text-foreground">
          {message.text}
        </div>
      </div>
    );
  }

  return (
    <div className="flex justify-start">
      <div
        className={cn(
          "max-w-[90%] rounded-2xl rounded-bl-sm border px-3 py-2 text-sm",
          message.error
            ? "border-amber-400/30 bg-amber-500/10 text-amber-200"
            : "border-glass bg-glass text-foreground",
        )}
      >
        {message.html ? (
          <SafeHtml html={message.html} className="kb-chat-prose" />
        ) : (
          <p>{message.text}</p>
        )}
        {message.citations && message.citations.length > 0 && (
          <div className="mt-2 space-y-1 border-t border-glass pt-2">
            {message.citations.map((c) => (
              <Link
                key={c.articleId}
                to="/kb/articles/$articleId"
                params={{ articleId: c.articleId }}
                className="flex items-center gap-1.5 rounded-md px-1.5 py-1 text-xs text-violet-300 transition-colors hover:bg-glass-hover hover:text-violet-200"
              >
                <BookOpen className="h-3 w-3 shrink-0" />
                <span className="truncate">{c.title}</span>
              </Link>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
