import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";
import { toServerLocal, toServerLocalDate, useServerTime } from "@/hooks/useServerTime";
import { useTheme } from "@/app/ThemeProvider";
import { colorPillStyle } from "@/lib/colorPill";
import { portalAuthApi, type PortalMeUser } from "@/lib/portal-api";
import { cn } from "@/lib/utils";

export const PORTAL_ME_QK = ["portal", "me"] as const;

/// The signed-in customer's portal profile (contact / company / role).
/// Separate from authStore (which only knows id/email/role/amr).
export function usePortalMe() {
  const q = useQuery({
    queryKey: PORTAL_ME_QK,
    queryFn: () => portalAuthApi.me(),
    staleTime: 60_000,
  });
  return { ...q, user: (q.data?.user ?? null) as PortalMeUser | null };
}

export function useInvalidatePortalMe() {
  const qc = useQueryClient();
  return useCallback(() => qc.invalidateQueries({ queryKey: PORTAL_ME_QK }), [qc]);
}

/// Server-anchored date formatting for portal pages. Falls back to the
/// browser only while the first /api/system/time answer is in flight.
export function usePortalDates() {
  const { time } = useServerTime();
  const offset = time?.offsetMinutes ?? null;
  return {
    dateTime: (iso: string | null | undefined) =>
      !iso ? "" : offset === null ? new Date(iso).toLocaleString() : toServerLocal(iso, offset),
    date: (iso: string | null | undefined) =>
      !iso ? "" : offset === null ? new Date(iso).toLocaleDateString() : toServerLocalDate(iso, offset),
  };
}

export function StatusPill({ name, color, className }: { name: string; color: string; className?: string }) {
  const theme = useTheme();
  return (
    <span
      className={cn("inline-flex items-center rounded-full border px-2 py-0.5 text-[11px] font-medium leading-4", className)}
      style={colorPillStyle(color || "#64748b", { family: theme.family, mode: theme.mode })}
    >
      {name}
    </span>
  );
}

export function PriorityDot({ name, color }: { name: string; color: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
      <span className="inline-block h-2 w-2 rounded-full" style={{ backgroundColor: color || "#64748b" }} />
      {name}
    </span>
  );
}

export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}
