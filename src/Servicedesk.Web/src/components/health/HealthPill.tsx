import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { systemApi, type HealthStatus } from "@/lib/api";
import { authStore } from "@/auth/authStore";

const HEALTH_QUERY_KEY = ["system", "health"] as const;
const REFETCH_MS = 30_000;

const STATUS_STYLES: Record<HealthStatus, { dot: string; label: string; text: string }> = {
  Ok: {
    dot: "bg-emerald-400 shadow-[0_0_8px_rgba(52,211,153,0.6)]",
    label: "All systems OK",
    text: "text-emerald-300",
  },
  Warning: {
    dot: "bg-amber-400 shadow-[0_0_8px_rgba(251,191,36,0.6)]",
    label: "Attention needed",
    text: "text-amber-300",
  },
  Critical: {
    dot: "bg-rose-500 shadow-[0_0_10px_rgba(244,63,94,0.7)]",
    label: "Critical",
    text: "text-rose-300",
  },
};

export function HealthPill() {
  const navigate = useNavigate();
  const { data, isLoading } = useQuery({
    queryKey: HEALTH_QUERY_KEY,
    queryFn: () => systemApi.health(),
    refetchInterval: REFETCH_MS,
    staleTime: REFETCH_MS / 2,
  });
  const role = authStore.get().user?.role;
  const isAdmin = role === "Admin";

  if (isLoading || !data) {
    return (
      <div className="inline-flex items-center gap-2 rounded-full border border-white/10 bg-white/[0.04] px-3 py-1.5 text-xs text-muted-foreground">
        <span className="h-2 w-2 rounded-full bg-white/20" />
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
      <div className="inline-flex items-center gap-2 rounded-full border border-white/10 bg-white/[0.04] px-3 py-1.5 text-xs">
        {content}
      </div>
    );
  }

  return (
    <button
      type="button"
      onClick={() => navigate({ to: "/settings/health" })}
      className="inline-flex items-center gap-2 rounded-full border border-white/10 bg-white/[0.04] px-3 py-1.5 text-xs transition-colors hover:border-white/20 hover:bg-white/[0.08]"
    >
      {content}
      <span className="text-muted-foreground">›</span>
    </button>
  );
}
