import { useQuery } from "@tanstack/react-query";
import { Wrench } from "lucide-react";
import { systemApi } from "@/lib/api";
import { useServerTime } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";

const MAINTENANCE_QUERY_KEY = ["system", "maintenance"] as const;

/**
 * App-wide maintenance warning bar. Public — works on the login page too,
 * because the underlying endpoint requires no auth. The server is the
 * single source of truth for whether the window is active; this component
 * just polls and renders.
 *
 * Variant "shell" sits at the top of the authenticated AppShell (full-width,
 * subtle border). Variant "auth" sits above the login card on /login
 * (rounded, centered, max-width matching the card).
 */
type Props = { variant?: "shell" | "auth"; className?: string };

export function MaintenanceBanner({ variant = "shell", className }: Props) {
  const { time } = useServerTime();
  const { data } = useQuery({
    queryKey: MAINTENANCE_QUERY_KEY,
    queryFn: () => systemApi.maintenance(),
    refetchInterval: 60_000,
  });

  if (!data?.active) return null;

  const offsetMinutes = time?.offsetMinutes ?? 0;
  const startLabel = data.startUtc ? formatLocal(data.startUtc, offsetMinutes) : null;
  const endLabel = data.endUtc ? formatLocal(data.endUtc, offsetMinutes) : null;

  const message =
    data.message?.trim() ||
    "Scheduled maintenance is in progress — service may be temporarily affected.";

  const window = formatWindow(startLabel, endLabel);

  if (variant === "auth") {
    return (
      <div
        className={cn(
          "mx-auto mb-4 flex w-full max-w-[420px] items-start gap-3 rounded-[var(--radius)] border border-amber-500/30 bg-amber-500/[0.08] px-4 py-3 text-xs text-amber-100 backdrop-blur",
          className,
        )}
        role="status"
      >
        <Wrench className="mt-0.5 h-4 w-4 shrink-0 text-amber-300" />
        <div className="space-y-1">
          <p className="font-medium uppercase tracking-wider text-amber-200/90 text-[10px]">
            Maintenance{window ? ` · ${window}` : ""}
          </p>
          <p className="leading-snug text-amber-100/90">{message}</p>
        </div>
      </div>
    );
  }

  return (
    <div
      className={cn(
        "border-b border-amber-500/30 bg-gradient-to-r from-amber-500/[0.10] via-amber-500/[0.07] to-transparent px-6 py-2 text-sm text-amber-100 backdrop-blur",
        className,
      )}
      role="status"
    >
      <div className="flex items-center gap-3">
        <Wrench className="h-4 w-4 shrink-0 text-amber-300" />
        <div className="flex-1 min-w-0">
          <span className="font-medium text-amber-200">Maintenance</span>
          {window && <span className="ml-2 text-amber-200/80">· {window}</span>}
          <span className="ml-3 text-amber-100/90">{message}</span>
        </div>
      </div>
    </div>
  );
}

function formatLocal(iso: string, offsetMinutes: number): string {
  // Server-local "yyyy-MM-dd HH:mm" — same convention as the picker.
  const localMs = new Date(iso).getTime() + offsetMinutes * 60_000;
  const d = new Date(localMs);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
}

function formatWindow(start: string | null, end: string | null): string | null {
  if (start && end) {
    // Same calendar day → collapse to "yyyy-MM-dd HH:mm – HH:mm".
    const startDate = start.slice(0, 10);
    const endDate = end.slice(0, 10);
    if (startDate === endDate) {
      return `${start} – ${end.slice(11)}`;
    }
    return `${start} – ${end}`;
  }
  if (end) return `until ${end}`;
  if (start) return `from ${start}`;
  return null;
}
