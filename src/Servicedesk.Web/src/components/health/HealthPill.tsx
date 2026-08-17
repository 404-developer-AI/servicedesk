import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { systemApi, type HealthStatus } from "@/lib/api";
import { pollIntervalMs } from "@/lib/healthPolling";
import { authStore } from "@/auth/authStore";

const HEALTH_QUERY_KEY = ["system", "health"] as const;

// Light mode reads the dot+label as deeper jewel tones (700) for contrast on
// a near-white pill; dark mode keeps the existing pale-300 glow palette.
const STATUS_STYLES: Record<HealthStatus, { dot: string; label: string; text: string }> = {
  Ok: {
    dot: "bg-emerald-500 dark:bg-emerald-400 shadow-[0_0_8px_rgba(52,211,153,0.6)]",
    label: "All systems OK",
    text: "text-emerald-700 dark:text-emerald-300",
  },
  Warning: {
    dot: "bg-amber-500 dark:bg-amber-400 shadow-[0_0_8px_rgba(251,191,36,0.6)]",
    label: "Attention needed",
    text: "text-amber-700 dark:text-amber-300",
  },
  Critical: {
    dot: "bg-rose-600 dark:bg-rose-500 shadow-[0_0_10px_rgba(244,63,94,0.7)]",
    label: "Critical",
    text: "text-rose-700 dark:text-rose-300",
  },
};

export function HealthPill() {
  const navigate = useNavigate();
  const { data, isLoading } = useQuery({
    queryKey: HEALTH_QUERY_KEY,
    queryFn: () => systemApi.health(),
    // Cadence is server-dictated (Health.PollIntervalSeconds), see healthPolling.ts.
    refetchInterval: (q) => pollIntervalMs(q.state.data),
    staleTime: (q) => pollIntervalMs(q.state.data) / 2,
  });
  const role = authStore.get().user?.role;
  const isAdmin = role === "Admin";

  if (isLoading || !data) {
    return (
      <div className="inline-flex items-center gap-2 rounded-full border border-glass bg-glass px-3 py-1.5 text-xs text-muted-foreground">
        <span className="h-2 w-2 rounded-full bg-glass-strong" />
        Checking health…
      </div>
    );
  }

  const style = STATUS_STYLES[data.status];
  const content = (
    <>
      <span className={`h-2 w-2 rounded-full ${style.dot}`} />
      <span className={style.text}>{style.label}</span>
    </>
  );

  if (!isAdmin) {
    return (
      <div className="inline-flex items-center gap-2 rounded-full border border-glass bg-glass px-3 py-1.5 text-xs">
        {content}
      </div>
    );
  }

  return (
    <button
      type="button"
      onClick={() => navigate({ to: "/settings/health" })}
      className="inline-flex items-center gap-2 rounded-full border border-glass bg-glass px-3 py-1.5 text-xs transition-colors hover:border-glass-strong hover:bg-glass-hover"
    >
      {content}
      <span className="text-muted-foreground">›</span>
    </button>
  );
}
