import { cn } from "@/lib/utils";
import type { M365LinkStatus } from "@/lib/api";

/// Visual treatment per connection status. Mirrors the glass status pills used
/// by the integration tiles so the matching list reads consistently.
const STATUS: Record<
  M365LinkStatus | "not-connected",
  { label: string; pill: string; dot: string }
> = {
  connected: {
    label: "Connected",
    pill: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    dot: "bg-emerald-400",
  },
  needs_reconsent: {
    label: "Needs re-consent",
    pill: "border-amber-400/30 bg-amber-500/[0.08] text-amber-200",
    dot: "bg-amber-400",
  },
  error: {
    label: "Sync error",
    pill: "border-rose-400/40 bg-rose-500/10 text-rose-300",
    dot: "bg-rose-400",
  },
  disconnected: {
    label: "Not connected",
    pill: "border-glass-strong bg-glass-strong text-muted-foreground",
    dot: "bg-glass-strong",
  },
  "not-connected": {
    label: "Not connected",
    pill: "border-glass-strong bg-glass-strong text-muted-foreground",
    dot: "bg-glass-strong",
  },
};

export function M365StatusBadge({
  status,
  className,
}: {
  status: M365LinkStatus | null | undefined;
  className?: string;
}) {
  const style = STATUS[status ?? "not-connected"] ?? STATUS["not-connected"];
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2 py-0.5 text-[11px] font-normal",
        style.pill,
        className,
      )}
    >
      <span className={cn("h-1.5 w-1.5 rounded-full", style.dot)} />
      {style.label}
    </span>
  );
}

/// A connection is "live" (no Connect button) only when fully connected.
export function isM365Connected(status: M365LinkStatus | null | undefined): boolean {
  return status === "connected";
}

/// Open the consent URL without tripping the popup blocker: a window is opened
/// synchronously inside the user gesture, then navigated once the server has
/// minted the state + URL. The customer's global admin signs in there; when the
/// agent returns to this tab, the connection list refetches on window focus.
export async function openConsentWindow(
  fetchUrl: () => Promise<{ consentUrl: string }>,
): Promise<void> {
  // Open synchronously (no "noopener" — that would return null and cost us the
  // handle) so the popup blocker stays happy, then navigate once the server has
  // minted the state + URL. Sever the opener link defensively against reverse
  // tabnabbing before pointing it at the Microsoft sign-in page.
  const w = window.open("", "_blank");
  if (w) w.opener = null;
  try {
    const { consentUrl } = await fetchUrl();
    if (w) w.location.href = consentUrl;
    else window.location.href = consentUrl;
  } catch (err) {
    if (w) w.close();
    throw err;
  }
}

/// "synced 5m ago" style relative copy from a server timestamp.
export function relativeFromUtc(iso: string | null | undefined): string | null {
  if (!iso) return null;
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return null;
  const diffMs = Date.now() - then;
  const mins = Math.round(diffMs / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  return `${days}d ago`;
}
