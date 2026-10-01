import { useQuery } from "@tanstack/react-query";
import type { CSSProperties } from "react";
import { ticketApi, type TicketFlagSettings, type TicketListItem, type DisplayConfig } from "@/lib/ticket-api";

/// v0.1.17 — Call-back / Research ticket flags. Colours are admin settings
/// (Settings → Tickets → General); these fallbacks match the registered
/// defaults so a failed settings read still paints sensible accents.
export const DEFAULT_CALLBACK_COLOR = "#22c55e";
export const DEFAULT_RESEARCH_COLOR = "#3b82f6";
export const PRIORITY_FLOAT_COLOR = "#ef4444";

export const ticketFlagSettingsKey = ["settings", "ticket-flags"] as const;

export function useTicketFlagSettings() {
  return useQuery<TicketFlagSettings>({
    queryKey: ticketFlagSettingsKey,
    queryFn: ticketApi.ticketFlagSettings,
    staleTime: 5 * 60_000,
  });
}

export function flagColors(settings: TicketFlagSettings | undefined) {
  return {
    callback: settings?.callbackColor ?? DEFAULT_CALLBACK_COLOR,
    research: settings?.researchColor ?? DEFAULT_RESEARCH_COLOR,
  };
}

/// Float bucket a ticket lands in for a view: 0 = Priority, 1 = Call-back,
/// 2 = Research, null = normal list. Mirrors the server ORDER BY in
/// TicketRepository.SearchAsync: only New/Open tickets float, fixed
/// precedence Priority > Call-back > Research, and a ticket sits only in the
/// first *enabled* bucket it qualifies for.
export function floatBucket(
  t: Pick<TicketListItem, "priorityIsDefault" | "isCallback" | "isResearch" | "statusStateCategory">,
  dc: Pick<DisplayConfig, "priorityFloat" | "callbackFloat" | "researchFloat">,
): 0 | 1 | 2 | null {
  if (t.statusStateCategory !== "New" && t.statusStateCategory !== "Open") return null;
  if (dc.priorityFloat && !t.priorityIsDefault) return 0;
  if (dc.callbackFloat && t.isCallback) return 1;
  if (dc.researchFloat && t.isResearch) return 2;
  return null;
}

/// Row accent (3px left bar + left-to-right fading glow). A non-default
/// priority wins, then Call-back, then Research — the same precedence as the
/// floats. Plain tickets keep the bar in their (default) priority colour and
/// get no glow, exactly as before v0.1.17.
export function ticketRowAccentStyle(
  t: Pick<TicketListItem, "priorityColor" | "priorityIsDefault" | "isCallback" | "isResearch">,
  colors: { callback: string; research: string },
): CSSProperties {
  let color = t.priorityColor || "#6b7280";
  let glow = false;
  if (!t.priorityIsDefault && t.priorityColor) {
    glow = true;
  } else if (t.isCallback) {
    color = colors.callback;
    glow = true;
  } else if (t.isResearch) {
    color = colors.research;
    glow = true;
  }
  return {
    boxShadow: `inset 3px 0 0 0 ${color}`,
    ...(glow
      ? { backgroundImage: `linear-gradient(to right, ${color}12 0%, ${color}06 30%, transparent 60%)` }
      : {}),
  };
}
