import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import DOMPurify from "dompurify";
import { Info, AlertTriangle, AlertCircle } from "lucide-react";
import { systemApi, type LoginBannerType } from "@/lib/api";
import { cn } from "@/lib/utils";

const LOGIN_BANNER_QUERY_KEY = ["system", "login-banner"] as const;

/**
 * Admin-controlled notice shown above the login card on every anonymous
 * auth page. Public — the underlying endpoint requires no auth, so this
 * works at /login, /auth/microsoft callbacks, and (v0.1.x) the customer
 * portal login. The server is the single source of truth for visibility,
 * type and message; this component only polls and renders.
 *
 * Message supports a constrained Markdown subset (bold, italic, links)
 * which is escaped, transformed and then sanitized through DOMPurify with
 * a strict allow-list before being rendered. Links are forced to
 * target="_blank" rel="noopener noreferrer" and only http/https/mailto
 * schemes are kept.
 */
type Props = { className?: string };

export function LoginBanner({ className }: Props) {
  const { data } = useQuery({
    queryKey: LOGIN_BANNER_QUERY_KEY,
    queryFn: () => systemApi.loginBanner(),
    refetchInterval: 60_000,
  });

  const html = useMemo(
    () => (data?.enabled ? renderBannerMarkdown(data.message) : null),
    [data?.enabled, data?.message],
  );

  if (!data?.enabled || !html) return null;

  const palette = PALETTES[data.type] ?? PALETTES.info;
  const Icon = palette.icon;

  return (
    <div
      role="status"
      className={cn(
        "mx-auto mb-4 flex w-full max-w-[420px] items-start gap-3 rounded-[var(--radius)] border px-4 py-3 text-xs backdrop-blur",
        palette.container,
        className,
      )}
    >
      <Icon className={cn("mt-0.5 h-4 w-4 shrink-0", palette.icon_)} />
      <div className="space-y-1 min-w-0">
        <p className={cn("font-medium uppercase tracking-wider text-[10px]", palette.label)}>
          {palette.title}
        </p>
        <div
          className={cn("leading-snug break-words", palette.body)}
          dangerouslySetInnerHTML={{ __html: html }}
        />
      </div>
    </div>
  );
}

type Palette = {
  container: string;
  icon: typeof Info;
  icon_: string;
  label: string;
  body: string;
  title: string;
};

// Palette pairs: light-mode reads dark text on a pale tinted background;
// dark-mode reads pale text on a translucent tint over the dark canvas.
// Both keep the same hue so the semantic colour (info=sky, warning=amber,
// error=rose) is recognisable regardless of theme.
const PALETTES: Record<LoginBannerType, Palette> = {
  info: {
    container:
      "border-sky-400/50 bg-sky-50 text-sky-900 " +
      "dark:border-sky-500/30 dark:bg-sky-500/[0.08] dark:text-sky-100",
    icon: Info,
    icon_: "text-sky-600 dark:text-sky-300",
    label: "text-sky-700 dark:text-sky-200/90",
    body:
      "text-sky-900 [&_a]:underline [&_a]:underline-offset-2 [&_a]:text-sky-700 [&_a:hover]:text-sky-900 " +
      "dark:text-sky-100/90 dark:[&_a]:text-sky-100 dark:[&_a:hover]:text-white",
    title: "Notice",
  },
  warning: {
    container:
      "border-amber-400/60 bg-amber-50 text-amber-900 " +
      "dark:border-amber-500/30 dark:bg-amber-500/[0.08] dark:text-amber-100",
    icon: AlertTriangle,
    icon_: "text-amber-600 dark:text-amber-300",
    label: "text-amber-700 dark:text-amber-200/90",
    body:
      "text-amber-900 [&_a]:underline [&_a]:underline-offset-2 [&_a]:text-amber-700 [&_a:hover]:text-amber-900 " +
      "dark:text-amber-100/90 dark:[&_a]:text-amber-100 dark:[&_a:hover]:text-white",
    title: "Warning",
  },
  error: {
    container:
      "border-rose-400/60 bg-rose-50 text-rose-900 " +
      "dark:border-rose-500/40 dark:bg-rose-500/[0.10] dark:text-rose-100",
    icon: AlertCircle,
    icon_: "text-rose-600 dark:text-rose-300",
    label: "text-rose-700 dark:text-rose-200/90",
    body:
      "text-rose-900 [&_a]:underline [&_a]:underline-offset-2 [&_a]:text-rose-700 [&_a:hover]:text-rose-900 " +
      "dark:text-rose-100/90 dark:[&_a]:text-rose-100 dark:[&_a:hover]:text-white",
    title: "Error",
  },
};

/**
 * Transform the admin-authored message into a sanitized HTML fragment.
 * Pipeline:
 *   1. HTML-escape the entire string so any raw `<`/`>` the admin typed
 *      cannot reach the DOM as a tag.
 *   2. Apply a minimal Markdown transform (links, bold, italic, newlines).
 *      Link href is whitelisted to http/https/mailto and the anchor is
 *      pre-decorated with target+rel; DOMPurify will not strip those.
 *   3. Run the result through DOMPurify with a strict allow-list as the
 *      final defence.
 */
function renderBannerMarkdown(raw: string): string {
  const trimmed = (raw ?? "").trim();
  if (!trimmed) return "";

  let s = escapeHtml(trimmed);

  // Links: [label](href). Label may itself contain bold/italic syntax,
  // which the later passes will resolve. We do links first so a stray `*`
  // in the URL or label isn't misread as italic.
  s = s.replace(LINK_RE, (_match, label: string, href: string) => {
    const safeHref = sanitizeHref(href);
    if (!safeHref) {
      return escapeHtml(label);
    }
    return `<a href="${safeHref}" target="_blank" rel="noopener noreferrer">${label}</a>`;
  });

  // Bold (**text**) then italic (*text* or _text_). Non-greedy, on a
  // single line — these patterns intentionally do not span newlines.
  s = s.replace(/\*\*([^*\n]+?)\*\*/g, "<strong>$1</strong>");
  s = s.replace(/(^|[^*])\*([^*\n]+?)\*(?!\*)/g, "$1<em>$2</em>");
  s = s.replace(/(^|[^_])_([^_\n]+?)_(?!_)/g, "$1<em>$2</em>");

  s = s.replace(/\r?\n/g, "<br>");

  return DOMPurify.sanitize(s, {
    ALLOWED_TAGS: ["strong", "em", "a", "br"],
    ALLOWED_ATTR: ["href", "target", "rel"],
  }) as unknown as string;
}

const LINK_RE = /\[([^\]\n]+)\]\(([^\s)]+)\)/g;

function escapeHtml(input: string): string {
  return input
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function sanitizeHref(href: string): string | null {
  // href is already HTML-escaped (we ran escapeHtml on the whole string
  // before this point), so look at the un-escaped scheme prefix.
  const raw = href.replace(/&amp;/g, "&").trim();
  if (/^https?:\/\//i.test(raw) || /^mailto:/i.test(raw)) {
    return href;
  }
  return null;
}

/// Render an admin's draft message into the same banner-internal HTML the
/// public component renders, so the Settings page can show a real preview.
/// Exposed for the settings preview only — production rendering goes
/// through the LoginBanner component above.
export function renderLoginBannerPreviewHtml(raw: string): string {
  return renderBannerMarkdown(raw);
}
